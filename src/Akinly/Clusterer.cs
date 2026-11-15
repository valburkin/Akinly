using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Akinly.Engine;
using Akinly.Storage;
using Akinly.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Akinly;

/// <summary>
/// Clusters items as they arrive, keeping its state in an <see cref="IClusterStore"/>. Clear
/// cases are settled at once; close calls come back as <see cref="Question"/>s for a judge, and
/// their answers are applied whenever they arrive.
/// </summary>
/// <remarks>
/// <para>
/// Each call reads what it needs, decides, and writes one change set. When the store refuses
/// the write because another writer came first, the call waits a moment (a random backoff that
/// grows with each attempt, so competing writers don't keep colliding) and reads again, up to
/// 16 times. Calls on one instance run one at a time.
/// </para>
/// <para>
/// The same calls in the same order, with the same answers, give the same result when the
/// store's nearest-cluster search is exact (as the in-memory store's is).
/// </para>
/// </remarks>
[SuppressMessage(
    "Reliability",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "The SemaphoreSlim only serializes calls; it holds no unmanaged resource unless its AvailableWaitHandle is used, which it never is.")]
public sealed class Clusterer
{
    private const int MaxConflicts = 16;

    private readonly ClusteringOptions _options;
    private readonly IMergePolicy? _mergePolicy;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>A clusterer over a store.</summary>
    /// <param name="store">Where the clusters, items and questions are kept.</param>
    /// <param name="options">How it decides; copied, so later changes to the instance have no effect.</param>
    /// <param name="logger">Where it logs; nowhere when null.</param>
    /// <param name="mergePolicy">Your rules for merges (which clusters never merge, which keeps its id); none when null.</param>
    /// <exception cref="ArgumentException">The options are invalid.</exception>
    public Clusterer(IClusterStore store, ClusteringOptions options, ILogger<Clusterer>? logger = null, IMergePolicy? mergePolicy = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        options.ThrowIfInvalid();
        Store = store;
        _options = options.Clone();
        _logger = logger ?? NullLogger<Clusterer>.Instance;
        _mergePolicy = mergePolicy;
    }

    /// <summary>The store the clusterer works on.</summary>
    public IClusterStore Store { get; }

    /// <summary>A copy of the options the clusterer uses.</summary>
    public ClusteringOptions Options => _options.Clone();

    /// <summary>
    /// Adds items and places them: each joins its nearest cluster, waits for a judge's answer,
    /// or starts a cluster. Items waiting for room to ask are placed first.
    /// </summary>
    /// <param name="items">The items, in the order to place them; ids must be new to the store.</param>
    /// <param name="cancellationToken">Cancels the work; nothing is written then.</param>
    /// <returns>The placements made and the questions asked.</returns>
    /// <exception cref="ArgumentException">A null item, an empty or repeated id, an id already in the store, or a zero or non-finite embedding.</exception>
    /// <exception cref="EmbeddingModelMismatchException">The store holds another model's embeddings, or an embedding's length differs.</exception>
    public Task<ClusteringStep> AddAsync(IEnumerable<ClusterItem> items, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        var list = items.ToList();
        return RunAsync("add", session => session.AddAsync(list), cancellationToken);
    }

    /// <summary>
    /// Applies a judge's answers. Answers to unknown or closed questions are ignored, and only
    /// the first answer to a question counts, so answers may arrive late, out of order, or twice.
    /// </summary>
    /// <param name="verdicts">The answers, in any order.</param>
    /// <param name="cancellationToken">Cancels the work; nothing is written then.</param>
    /// <returns>The placements and merges the answers made, and any follow-up questions.</returns>
    public async Task<ClusteringStep> AnswerAsync(IEnumerable<Verdict> verdicts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(verdicts);
        var list = verdicts.ToList();
        if (list.Exists(v => !Enum.IsDefined(v.Kind)))
        {
            throw new ArgumentException("An answer has an undefined kind.", nameof(verdicts));
        }

        var step = await RunAsync("answer", session => session.AnswerAsync(list), cancellationToken).ConfigureAwait(false);
        foreach (var verdict in list)
        {
            AkinlyTelemetry.Verdicts.Add(1, new KeyValuePair<string, object?>("kind", AkinlyTelemetry.Tag(verdict.Kind)));
        }

        return step;
    }

    /// <summary>
    /// Compares the clusters that changed since the last call with their nearest neighbors:
    /// merges the certain pairs, asks a judge about the close ones, splits loose clusters when
    /// <see cref="ClusteringOptions.Split"/> is on, and recomputes centroids after many removals.
    /// Call it every so often, such as after each batch.
    /// </summary>
    /// <param name="cancellationToken">Cancels the work; nothing is written then.</param>
    /// <returns>The merges and splits made, the questions asked, and any waiting items placed.</returns>
    public Task<ClusteringStep> MaintainAsync(CancellationToken cancellationToken = default) =>
        RunAsync("maintain", session => session.MaintainAsync(), cancellationToken);

    /// <summary>
    /// Removes items, for example because they were deleted at their source: their clusters'
    /// centroids are recomputed, and their open questions closed. Unknown ids are ignored. A
    /// cluster left empty keeps its id and its last centroid, so a returning item can still join it.
    /// </summary>
    /// <param name="itemIds">The items to remove.</param>
    /// <param name="cancellationToken">Cancels the work; nothing is written then.</param>
    public Task RemoveAsync(IEnumerable<string> itemIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        var list = itemIds.ToList();
        if (list.Exists(id => id is null))
        {
            throw new ArgumentException("The list holds a null id.", nameof(itemIds));
        }

        return RunAsync("remove", session => session.RemoveAsync(list), cancellationToken);
    }

    /// <summary>
    /// Sets a cluster's label (a one-sentence title), which judges see with its questions, and
    /// records the cluster's size at that moment (<see cref="Cluster.LabeledAtCount"/>).
    /// </summary>
    /// <param name="clusterId">The cluster; a merged cluster's id is followed to the cluster it merged into.</param>
    /// <param name="label">The label, or null to clear it.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>False when there is no such cluster.</returns>
    public async Task<bool> SetLabelAsync(long clusterId, string? label, CancellationToken cancellationToken = default)
    {
        var found = false;
        await RunAsync("label", async session => found = await session.SetLabelAsync(clusterId, label).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        return found;
    }

    /// <summary>The questions still waiting for an answer, by id: put them to a judge.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    public async Task<IReadOnlyList<Question>> GetOpenQuestionsAsync(CancellationToken cancellationToken = default)
    {
        var open = await Store.GetOpenQuestionsAsync(cancellationToken).ConfigureAwait(false);
        return [.. open.Select(q => q.Question)];
    }

    /// <summary>A cluster by id. A merged cluster's id leads to the cluster it merged into.</summary>
    /// <param name="clusterId">The cluster's id.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The cluster, or null when there is none with that id.</returns>
    public async Task<Cluster?> GetClusterAsync(long clusterId, CancellationToken cancellationToken = default)
    {
        var session = await Session.StartAsync(Store, _options, _mergePolicy, cancellationToken).ConfigureAwait(false);
        return (await session.ResolveAsync(clusterId).ConfigureAwait(false))?.ToCluster();
    }

    /// <summary>Every live cluster, by id.</summary>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    public async IAsyncEnumerable<Cluster> ListClustersAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var cluster in Store.ListClustersAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return cluster.ToCluster();
        }
    }

    /// <summary>The ids of a cluster's members, in the order they were added.</summary>
    /// <param name="clusterId">The cluster; a merged cluster's id leads to the cluster it merged into.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    public async Task<IReadOnlyList<string>> GetMembersAsync(long clusterId, CancellationToken cancellationToken = default)
    {
        var session = await Session.StartAsync(Store, _options, _mergePolicy, cancellationToken).ConfigureAwait(false);
        if (await session.ResolveAsync(clusterId).ConfigureAwait(false) is not { } cluster)
        {
            return [];
        }

        var members = await Store.GetMembersAsync(cluster.Id, cancellationToken).ConfigureAwait(false);
        return [.. members.Select(m => m.Id)];
    }

    /// <summary>The cluster an item is in, or null while it waits (or when it is not in the store).</summary>
    /// <param name="itemId">The item.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    public async Task<long?> GetClusterOfAsync(string itemId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(itemId);
        var items = await Store.GetItemsAsync([itemId], cancellationToken).ConfigureAwait(false);
        return items.Count > 0 && items[0].State == ItemState.Placed ? items[0].ClusterId : null;
    }

    private async Task<ClusteringStep> RunAsync(string operation, Func<Session, Task> work, CancellationToken cancellationToken)
    {
        using var activity = AkinlyTelemetry.Source.StartActivity($"akinly.{operation}");
        var started = Stopwatch.GetTimestamp();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                var session = await Session.StartAsync(Store, _options, _mergePolicy, cancellationToken).ConfigureAwait(false);
                await work(session).ConfigureAwait(false);
                var changes = session.Changes();
                if (changes is not null)
                {
                    try
                    {
                        await Store.ApplyAsync(changes, session.BaseVersion, cancellationToken).ConfigureAwait(false);
                    }
                    catch (ClusterStoreConflictException conflict) when (attempt < MaxConflicts)
                    {
                        AkinlyTelemetry.Conflicts.Add(1);
                        Log.Conflict(_logger, operation, conflict.ActualVersion ?? session.BaseVersion, attempt);
                        await Task.Delay(Backoff(attempt), cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    catch (ClusterStoreConflictException)
                    {
                        AkinlyTelemetry.Conflicts.Add(1);
                        Log.GaveUp(_logger, operation, attempt);
                        throw;
                    }
                }

                var step = session.Step();
                Record(operation, step, changes?.Info.Version ?? session.BaseVersion);
                activity?.SetTag("akinly.placed", step.Placements.Count);
                activity?.SetTag("akinly.questions", step.Questions.Count);
                return step;
            }
        }
        finally
        {
            _gate.Release();
            AkinlyTelemetry.Duration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, new KeyValuePair<string, object?>("operation", operation));
        }
    }

    /// <summary>
    /// A random wait of up to 2^attempt milliseconds (at most 128): writers that collided pick
    /// different moments to try again. Timing only; the result does not depend on it.
    /// </summary>
    private static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1 << Math.Min(attempt, 7)));

    private void Record(string operation, ClusteringStep step, long version)
    {
        foreach (var placement in step.Placements)
        {
            AkinlyTelemetry.ItemsPlaced.Add(1, new KeyValuePair<string, object?>("reason", AkinlyTelemetry.Tag(placement.Reason)));
        }

        foreach (var question in step.Questions)
        {
            AkinlyTelemetry.QuestionsAsked.Add(1, new KeyValuePair<string, object?>("kind", AkinlyTelemetry.Tag(question)));
        }

        foreach (var merge in step.Merges)
        {
            AkinlyTelemetry.ClustersMerged.Add(1, new KeyValuePair<string, object?>("reason", AkinlyTelemetry.Tag(merge.Reason)));
        }

        if (step.Splits.Count > 0)
        {
            AkinlyTelemetry.ClustersSplit.Add(step.Splits.Count);
        }

        Log.StepApplied(_logger, operation, step.Placements.Count, step.Merges.Count, step.Splits.Count, step.Questions.Count, version);
    }
}
