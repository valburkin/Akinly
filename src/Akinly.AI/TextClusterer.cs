using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Akinly.Storage;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Akinly.AI;

/// <summary>
/// Texts in, clusters out: embeds texts with any <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/>,
/// places them with a <see cref="Akinly.Clusterer"/>, has any <see cref="IChatClient"/> decide the
/// close calls and label the clusters, and keeps the texts in an <see cref="IItemTextStore"/> for
/// the judge.
/// </summary>
/// <remarks>
/// <para>
/// Each <see cref="AddAsync"/> settles every question it raises before it returns, so it waits on
/// the model; for answers through a batch API, use <see cref="Akinly.Clusterer"/> and
/// <see cref="JudgePrompts"/> directly. Calls on one instance run one at a time.
/// </para>
/// <para>
/// If <see cref="AddAsync"/> throws after the items were added (the judge's loop was canceled,
/// say), the items stay added and their questions open: call <see cref="SettleAsync"/> to finish,
/// rather than adding them again.
/// </para>
/// </remarks>
[SuppressMessage(
    "Reliability",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "The SemaphoreSlim only serializes calls; it holds no unmanaged resource unless its AvailableWaitHandle is used, which it never is.")]
public sealed class TextClusterer
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder;
    private readonly IClusterJudge? _judge;
    private readonly ChatClientLabeler? _labeler;
    private readonly TextClustererOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>A text clusterer with its own clusterer, judge and labeler, over a store (in memory when null).</summary>
    /// <param name="embedder">The embedding model.</param>
    /// <param name="chat">
    /// The model that decides close calls and writes labels; null for cosine-only clustering
    /// (then <paramref name="options"/> must have no ask zone, such as from
    /// <see cref="ClusteringOptions.CosineOnly"/>).
    /// </param>
    /// <param name="options">
    /// How the clusterer decides. When <see cref="ClusteringOptions.EmbeddingModel"/> is empty, the
    /// embedder's default model id is used.
    /// </param>
    /// <param name="textOptions">How the steps around the clusterer run; the defaults when null.</param>
    /// <param name="store">Where to keep the clusters; a new in-memory store when null.</param>
    /// <param name="texts">Where to keep the texts; a new in-memory store when null.</param>
    /// <param name="loggerFactory">Where to log; nowhere when null.</param>
    /// <exception cref="ArgumentException">The options are invalid, or they have an ask zone but no chat client was given.</exception>
    public TextClusterer(
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        IChatClient? chat,
        ClusteringOptions options,
        TextClustererOptions? textOptions = null,
        IClusterStore? store = null,
        IItemTextStore? texts = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(embedder);
        ArgumentNullException.ThrowIfNull(options);
        textOptions ??= new TextClustererOptions();
        Validate(textOptions);
        var resolved = options.Clone();
        if (string.IsNullOrWhiteSpace(resolved.EmbeddingModel))
        {
            resolved.EmbeddingModel = embedder.GetService<EmbeddingGeneratorMetadata>()?.DefaultModelId ?? "";
        }

        if (chat is null && resolved.HasAskZone)
        {
            throw new ArgumentException(
                "These options leave close calls to a judge, but no chat client was given. Pass one, or use ClusteringOptions.CosineOnly.",
                nameof(chat));
        }

        _embedder = embedder;
        _options = textOptions;
        Texts = texts ?? new InMemoryItemTextStore();
        Clusterer = new Clusterer(store ?? new InMemoryClusterStore(), resolved, loggerFactory?.CreateLogger<Clusterer>());
        if (chat is not null)
        {
            _judge = resolved.HasAskZone ? new ChatClientJudge(chat, Texts, textOptions.Judge, loggerFactory?.CreateLogger<ChatClientJudge>()) : null;
            _labeler = textOptions.LabelClusters ? new ChatClientLabeler(chat, Texts, textOptions.Labeler, loggerFactory?.CreateLogger<ChatClientLabeler>()) : null;
        }
    }

    /// <summary>A text clusterer over parts you made yourself, such as from dependency injection.</summary>
    /// <param name="clusterer">The clusterer.</param>
    /// <param name="embedder">The embedding model.</param>
    /// <param name="texts">Where texts are kept for the judge and the labeler; they must read from the same store.</param>
    /// <param name="judge">Decides close calls; null for cosine-only clustering.</param>
    /// <param name="labeler">Labels clusters; null for none.</param>
    /// <param name="textOptions">How the steps around the clusterer run; the defaults when null (its judge and labeler options are not used here).</param>
    public TextClusterer(
        Clusterer clusterer,
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        IItemTextStore texts,
        IClusterJudge? judge,
        ChatClientLabeler? labeler,
        TextClustererOptions? textOptions = null)
    {
        ArgumentNullException.ThrowIfNull(clusterer);
        ArgumentNullException.ThrowIfNull(embedder);
        ArgumentNullException.ThrowIfNull(texts);
        textOptions ??= new TextClustererOptions();
        Validate(textOptions);
        if (judge is null && clusterer.Options.HasAskZone)
        {
            throw new ArgumentException("The clusterer leaves close calls to a judge, but none was given.", nameof(judge));
        }

        Clusterer = clusterer;
        _embedder = embedder;
        Texts = texts;
        _judge = judge;
        _labeler = textOptions.LabelClusters ? labeler : null;
        _options = textOptions;
    }

    /// <summary>The clusterer underneath, for anything this class does not wrap.</summary>
    public Clusterer Clusterer { get; }

    /// <summary>The texts kept for the judge and the labeler.</summary>
    public IItemTextStore Texts { get; }

    /// <summary>
    /// Embeds texts and clusters them, settling every question they raise with the judge; then
    /// checks for merges and labels the clusters that are due. If the store refuses the add, the
    /// texts are put back as they were; if it fails partway (a timeout, say), they are kept, since
    /// the store may still finish the write: add the items again, or remove them.
    /// </summary>
    /// <param name="items">The texts, with ids new to the store.</param>
    /// <param name="cancellationToken">Cancels the work; steps already applied stay applied.</param>
    /// <returns>Everything placed, merged, split and asked, as one step.</returns>
    /// <exception cref="ArgumentException">An item or its text is null, or an id repeats; checked before anything is embedded.</exception>
    public async Task<ClusteringStep> AddAsync(IEnumerable<TextItem> items, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        var list = items.ToList();
        if (list.Exists(i => i is null || i.Text is null || i.Id is null))
        {
            throw new ArgumentException("An item, its id or its text is null.", nameof(items));
        }

        if (list.GroupBy(i => i.Id, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } repeated)
        {
            throw new ArgumentException($"Item '{repeated.Key}' appears more than once.", nameof(items));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var embedded = new List<ClusterItem>(list.Count);
            foreach (var batch in list.Chunk(_options.EmbeddingBatchSize))
            {
                var embeddings = await _embedder.GenerateAsync(batch.Select(i => i.Text), cancellationToken: cancellationToken).ConfigureAwait(false);
                if (embeddings.Count != batch.Length)
                {
                    throw new InvalidOperationException($"The embedding model returned {embeddings.Count} embeddings for {batch.Length} texts.");
                }

                embedded.AddRange(batch.Select((item, i) => new ClusterItem(item.Id, embeddings[i].Vector)));
            }

            // Texts first, so the judge can read them. An add that wrote nothing (refused, say, for
            // an id already in the store) puts back what was there before, so no text is lost or
            // left over. After any other failure the store may still finish the write (a journaled
            // store completes it on its next one), so the texts stay with their items.
            var previous = new List<(string Id, string? Text)>(list.Count);
            ClusteringStep step;
            var writing = false;
            try
            {
                foreach (var item in list)
                {
                    var before = await Texts.GetTextAsync(item.Id, cancellationToken).ConfigureAwait(false);
                    previous.Add((item.Id, before));
                    await Texts.SetTextAsync(item.Id, item.Text, cancellationToken).ConfigureAwait(false);
                }

                writing = true;
                step = await Clusterer.AddAsync(embedded, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (!writing || WroteNothing(exception))
            {
                foreach (var (id, text) in previous)
                {
                    if (text is null)
                    {
                        await Texts.RemoveTextAsync(id, CancellationToken.None).ConfigureAwait(false);
                    }
                    else
                    {
                        await Texts.SetTextAsync(id, text, CancellationToken.None).ConfigureAwait(false);
                    }
                }

                throw;
            }

            return await FinishAsync(step, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Whether a failed add is known to have left the store as it was.</summary>
    private static bool WroteNothing(Exception exception) =>
        exception is ArgumentException or EmbeddingModelMismatchException or ClusterStoreConflictException;

    /// <summary>
    /// Settles open questions with the judge, checks for merges, and labels what is due: what
    /// <see cref="AddAsync"/> does after adding, for resuming after an interruption.
    /// </summary>
    /// <param name="cancellationToken">Cancels the work; steps already applied stay applied.</param>
    public async Task<ClusteringStep> SettleAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await FinishAsync(ClusteringStep.Empty, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Removes items and forgets their texts. The texts go first, so even an interrupted removal
    /// never leaves a removed item's text behind; an item whose removal did not finish stays in
    /// its cluster, decided by similarity alone, until you remove it again.
    /// </summary>
    /// <param name="itemIds">The items.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <exception cref="ArgumentException">The list holds a null id; checked before any text is removed.</exception>
    public async Task RemoveAsync(IEnumerable<string> itemIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        var list = itemIds.ToList();
        if (list.Any(id => id is null))
        {
            throw new ArgumentException("The list holds a null id.", nameof(itemIds));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var id in list)
            {
                await Texts.RemoveTextAsync(id, cancellationToken).ConfigureAwait(false);
            }

            await Clusterer.RemoveAsync(list, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Forgets an item's text but keeps the item: it stays in its cluster, and questions about
    /// it are decided by similarity alone. For texts you may keep only for a limited time.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether a text was kept for it.</returns>
    public ValueTask<bool> ForgetTextAsync(string itemId, CancellationToken cancellationToken = default) =>
        Texts.RemoveTextAsync(itemId, cancellationToken);

    /// <summary>Every live cluster, by id.</summary>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    public async IAsyncEnumerable<Cluster> ListClustersAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var cluster in Clusterer.ListClustersAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return cluster;
        }
    }

    /// <summary>The ids of a cluster's members, in the order they were added.</summary>
    /// <param name="clusterId">The cluster; a merged cluster's id leads to the cluster it merged into.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    public Task<IReadOnlyList<string>> GetMembersAsync(long clusterId, CancellationToken cancellationToken = default) =>
        Clusterer.GetMembersAsync(clusterId, cancellationToken);

    private async Task<ClusteringStep> FinishAsync(ClusteringStep step, CancellationToken cancellationToken)
    {
        step = await JudgeAsync(step, cancellationToken).ConfigureAwait(false);
        if (_options.MaintainAfterAdd)
        {
            step = await JudgeAsync(step.Then(await Clusterer.MaintainAsync(cancellationToken).ConfigureAwait(false)), cancellationToken).ConfigureAwait(false);
        }

        if (_labeler is not null)
        {
            var touched = step.Placements.Select(p => p.ClusterId)
                .Concat(step.Merges.Select(m => m.IntoId))
                .Concat(step.Splits.SelectMany(s => s.NewClusterIds.Prepend(s.ClusterId)));
            await _labeler.LabelAsync(Clusterer, touched, cancellationToken).ConfigureAwait(false);
        }

        return step;
    }

    private async Task<ClusteringStep> JudgeAsync(ClusteringStep step, CancellationToken cancellationToken) =>
        _judge is null ? step : step.Then(await Clusterer.SettleAsync(_judge, cancellationToken).ConfigureAwait(false));

    private static void Validate(TextClustererOptions options)
    {
        if (options.EmbeddingBatchSize < 1)
        {
            throw new ArgumentException("EmbeddingBatchSize must be at least 1.", nameof(options));
        }

        if (options.Judge is null || options.Labeler is null)
        {
            throw new ArgumentException("Judge and Labeler options are required.", nameof(options));
        }
    }
}
