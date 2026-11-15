using System.Numerics.Tensors;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Akinly.Storage;

/// <summary>
/// A store that keeps everything in memory, behind one lock. <see cref="ApplyAsync"/> is atomic
/// and checks the version, so several clusterers can share one instance. Nearest-cluster search
/// is an exact scan with SIMD, so results are reproducible. Save and restore it with
/// <see cref="ExportAsync"/> and <see cref="ImportAsync"/>.
/// </summary>
public sealed class InMemoryClusterStore : IClusterStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<long, StoredCluster> _clusters = [];
    private readonly Dictionary<string, StoredItem> _items = new(StringComparer.Ordinal);
    private readonly Dictionary<long, SortedSet<long>> _membersByCluster = [];
    private readonly Dictionary<long, string> _itemIdBySequence = [];
    private readonly SortedSet<long> _waiting = [];
    private readonly Dictionary<QuestionId, StoredQuestion> _questions = [];
    private readonly SortedSet<QuestionId> _openQuestions = [];
    private readonly Dictionary<(long, long), MergeCheck> _mergeChecks = [];
    private StoreInfo _info = StoreInfo.Empty;

    /// <inheritdoc />
    public Task<StoreInfo> GetInfoAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_info);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ClusterNeighbor>> NearestClustersAsync(ReadOnlyMemory<float> vector, int count, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        lock (_lock)
        {
            // Keep the best few in a heap whose root is the worst kept, so a scan of n clusters
            // costs O(n log count), not a full sort.
            var span = vector.Span;
            var best = new PriorityQueue<ClusterNeighbor, (double Similarity, long NegatedId)>(count + 1);
            if (count > 0)
            {
                foreach (var cluster in _clusters.Values)
                {
                    if (!cluster.IsLive || cluster.Centroid.Length != span.Length)
                    {
                        continue;
                    }

                    var neighbor = new ClusterNeighbor(cluster, TensorPrimitives.Dot(span, cluster.Centroid.Span));
                    var priority = (neighbor.Similarity, -cluster.Id);
                    if (best.Count < count)
                    {
                        best.Enqueue(neighbor, priority);
                    }
                    else if (best.TryPeek(out _, out var worst) && priority.CompareTo(worst) > 0)
                    {
                        best.DequeueEnqueue(neighbor, priority);
                    }
                }
            }

            IReadOnlyList<ClusterNeighbor> nearest = [.. best.UnorderedItems
                .Select(e => e.Element)
                .OrderByDescending(n => n.Similarity)
                .ThenBy(n => n.Cluster.Id)];
            return Task.FromResult(nearest);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<StoredCluster>> GetClustersAsync(IEnumerable<long> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        lock (_lock)
        {
            IReadOnlyList<StoredCluster> found = [.. ids.Distinct().Select(id => _clusters.GetValueOrDefault(id)).OfType<StoredCluster>()];
            return Task.FromResult(found);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<StoredCluster> ListClustersAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        StoredCluster[] live;
        lock (_lock)
        {
            live = [.. _clusters.Values.Where(c => c.IsLive).OrderBy(c => c.Id)];
        }

        foreach (var cluster in live)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return cluster;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<StoredCluster>> GetChangedClustersAsync(long afterId, int count, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        lock (_lock)
        {
            IReadOnlyList<StoredCluster> changed = [.. _clusters.Values.Where(c => c.IsLive && c.Changed && c.Id > afterId).OrderBy(c => c.Id).Take(count)];
            return Task.FromResult(changed);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<StoredItem>> GetItemsAsync(IEnumerable<string> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        lock (_lock)
        {
            IReadOnlyList<StoredItem> found = [.. ids.Distinct(StringComparer.Ordinal).Select(id => _items.GetValueOrDefault(id)).OfType<StoredItem>()];
            return Task.FromResult(found);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<StoredItem>> GetMembersAsync(long clusterId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<StoredItem>>(Members(clusterId));
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> GetExamplesAsync(long clusterId, int count, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        lock (_lock)
        {
            if (!_clusters.TryGetValue(clusterId, out var cluster))
            {
                return Task.FromResult<IReadOnlyList<string>>([]);
            }

            var centroid = cluster.Centroid;
            IReadOnlyList<string> examples = [.. Members(clusterId)
                .Select(m => (m.Id, m.Sequence, Similarity: TensorPrimitives.Dot(m.Embedding.Span, centroid.Span)))
                .OrderByDescending(m => m.Similarity)
                .ThenBy(m => m.Sequence)
                .Take(count)
                .Select(m => m.Id)];
            return Task.FromResult(examples);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<StoredItem>> GetWaitingItemsAsync(int count, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        lock (_lock)
        {
            IReadOnlyList<StoredItem> waiting = [.. _waiting.Take(count).Select(sequence => _items[_itemIdBySequence[sequence]])];
            return Task.FromResult(waiting);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<StoredQuestion>> GetQuestionsAsync(IEnumerable<QuestionId> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        lock (_lock)
        {
            IReadOnlyList<StoredQuestion> found = [.. ids.Distinct().Select(id => _questions.GetValueOrDefault(id)).OfType<StoredQuestion>()];
            return Task.FromResult(found);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<StoredQuestion>> GetOpenQuestionsAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            IReadOnlyList<StoredQuestion> open = [.. _openQuestions.Select(id => _questions[id])];
            return Task.FromResult(open);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<MergeCheck>> GetMergeChecksAsync(IEnumerable<(long ClusterId, long OtherClusterId)> pairs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        lock (_lock)
        {
            IReadOnlyList<MergeCheck> found = [.. pairs
                .Select(p => MergeCheck.Key(p.ClusterId, p.OtherClusterId))
                .Distinct()
                .Select(key => _mergeChecks.GetValueOrDefault(key))
                .OfType<MergeCheck>()];
            return Task.FromResult(found);
        }
    }

    /// <inheritdoc />
    public Task ApplyAsync(ChangeSet changes, long expectedVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        cancellationToken.ThrowIfCancellationRequested();
        if (changes.Info.Version != expectedVersion + 1)
        {
            throw new ArgumentException("A change set moves the store's version up by exactly one.", nameof(changes));
        }

        lock (_lock)
        {
            if (_info.Version != expectedVersion)
            {
                throw new ClusterStoreConflictException(expectedVersion, _info.Version);
            }

            Write(changes);
        }

        return Task.CompletedTask;
    }

    /// <summary>Writes the whole store as JSON: a snapshot to save and restore with <see cref="ImportAsync"/>.</summary>
    /// <param name="destination">Where to write.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    public Task ExportAsync(Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        StoreSnapshot snapshot;
        lock (_lock)
        {
            snapshot = new StoreSnapshot(
                StoreSnapshot.CurrentSchemaVersion,
                _info,
                [.. _clusters.Values.OrderBy(c => c.Id)],
                [.. _items.Values.OrderBy(i => i.Sequence)],
                [.. _questions.Values.OrderBy(q => q.Question.Id)],
                [.. _mergeChecks.Values.OrderBy(c => c.ClusterId).ThenBy(c => c.OtherClusterId)]);
        }

        return JsonSerializer.SerializeAsync(destination, snapshot, StoreJsonContext.Default.StoreSnapshot, cancellationToken);
    }

    /// <summary>Reads a store written by <see cref="ExportAsync"/>.</summary>
    /// <param name="source">Where to read.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A new store holding the snapshot.</returns>
    /// <exception cref="InvalidDataException">The snapshot is unreadable, or from a newer schema version.</exception>
    public static async Task<InMemoryClusterStore> ImportAsync(Stream source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        StoreSnapshot? snapshot;
        try
        {
            snapshot = await JsonSerializer.DeserializeAsync(source, StoreJsonContext.Default.StoreSnapshot, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new InvalidDataException("The snapshot is not a cluster store written by ExportAsync.", exception);
        }

        if (snapshot is null)
        {
            throw new InvalidDataException("The snapshot is empty.");
        }

        if (snapshot.SchemaVersion > StoreSnapshot.CurrentSchemaVersion)
        {
            throw new InvalidDataException($"The snapshot has schema version {snapshot.SchemaVersion}; this version of Akinly reads up to {StoreSnapshot.CurrentSchemaVersion}.");
        }

        Check(snapshot);

        var store = new InMemoryClusterStore();
        store.Write(new ChangeSet(snapshot.Info, snapshot.Clusters, snapshot.Items, [], snapshot.Questions, snapshot.MergeChecks));
        return store;
    }

    /// <summary>Refuses a snapshot whose parts are missing or contradict each other.</summary>
    private static void Check(StoreSnapshot snapshot)
    {
        if (snapshot.Info is null || snapshot.Clusters is null || snapshot.Items is null || snapshot.Questions is null || snapshot.MergeChecks is null)
        {
            throw new InvalidDataException("The snapshot is missing its info, clusters, items, questions or merge checks.");
        }

        var info = snapshot.Info;
        if (snapshot.Clusters.Any(c => c is null || c.RefusedBy is null || c.Id <= 0 || c.Id >= info.NextClusterId || c.Count < 0 || c.Centroid.Length != info.Dimensions)
            || snapshot.Clusters.Select(c => c.Id).Distinct().Count() != snapshot.Clusters.Count)
        {
            throw new InvalidDataException("The snapshot has a missing, repeated or out-of-range cluster.");
        }

        var clusterIds = snapshot.Clusters.Select(c => c.Id).ToHashSet();
        if (snapshot.Items.Any(i => i is null || string.IsNullOrEmpty(i.Id) || i.RefusedClusterIds is null || i.Embedding.Length != info.Dimensions
                || i.Sequence <= 0 || i.Sequence >= info.NextItemSequence || (i.ClusterId is { } c && !clusterIds.Contains(c)))
            || snapshot.Items.Select(i => i.Id).Distinct(StringComparer.Ordinal).Count() != snapshot.Items.Count
            || snapshot.Items.Select(i => i.Sequence).Distinct().Count() != snapshot.Items.Count)
        {
            throw new InvalidDataException("The snapshot has a missing, repeated or inconsistent item.");
        }

        if (snapshot.Questions.Any(q => q?.Question is null || q.Question.Id.Value <= 0 || q.Question.Id.Value >= info.NextQuestionId)
            || snapshot.Questions.Select(q => q.Question.Id).Distinct().Count() != snapshot.Questions.Count)
        {
            throw new InvalidDataException("The snapshot has a missing, repeated or out-of-range question.");
        }

        if (snapshot.Questions.Count(q => q.State == QuestionState.Open) != info.OpenQuestions
            || snapshot.Items.Count(i => i.State == ItemState.Waiting) != info.WaitingItems)
        {
            throw new InvalidDataException("The snapshot's counts of open questions or waiting items do not match its contents.");
        }

        if (snapshot.MergeChecks.Any(m => m is null))
        {
            throw new InvalidDataException("The snapshot has a missing merge check.");
        }

        // The parts must agree with each other, not only be well-formed.
        var clusters = snapshot.Clusters.ToDictionary(c => c.Id);
        foreach (var cluster in snapshot.Clusters)
        {
            if (cluster.Sum.Length != info.Dimensions || !Finite(cluster.Sum.Span) || !Finite(cluster.Centroid.Span))
            {
                throw new InvalidDataException($"Cluster {cluster.Id} has a sum or centroid of the wrong length, or not finite.");
            }

            var hops = 0;
            for (var current = cluster; current.MergedIntoId is { } next; current = clusters[next])
            {
                if (!clusters.ContainsKey(next) || ++hops > clusters.Count)
                {
                    throw new InvalidDataException($"Cluster {cluster.Id} redirects to a cluster that is missing, or in a loop.");
                }
            }
        }

        var openQuestions = snapshot.Questions.Where(q => q.State == QuestionState.Open).ToDictionary(q => q.Question.Id, q => q.Question);
        foreach (var item in snapshot.Items)
        {
            var consistent = item.State switch
            {
                ItemState.Placed => item.ClusterId is { } c && clusters[c].IsLive && item.OpenQuestionId is null,
                ItemState.Asking => item.ClusterId is null && item.OpenQuestionId is { } q
                    && openQuestions.GetValueOrDefault(q) is JoinQuestion join && join.ItemId == item.Id,
                ItemState.Waiting => item.ClusterId is null && item.OpenQuestionId is null,
                _ => false,
            };
            if (!consistent || !Finite(item.Embedding.Span))
            {
                throw new InvalidDataException($"Item '{item.Id}' is in a state its cluster or question contradicts.");
            }
        }

        var members = snapshot.Items.Where(i => i.State == ItemState.Placed).GroupBy(i => i.ClusterId!.Value).ToDictionary(g => g.Key, g => g.Count());
        if (snapshot.Clusters.Any(c => c.Count != members.GetValueOrDefault(c.Id)))
        {
            throw new InvalidDataException("A cluster's count does not match its members.");
        }
    }

    private static bool Finite(ReadOnlySpan<float> values)
    {
        foreach (var value in values)
        {
            if (!float.IsFinite(value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Finite(ReadOnlySpan<double> values)
    {
        foreach (var value in values)
        {
            if (!double.IsFinite(value))
            {
                return false;
            }
        }

        return true;
    }

    private List<StoredItem> Members(long clusterId) =>
        _membersByCluster.TryGetValue(clusterId, out var sequences)
            ? [.. sequences.Select(sequence => _items[_itemIdBySequence[sequence]])]
            : [];

    private void Write(ChangeSet changes)
    {
        foreach (var cluster in changes.Clusters)
        {
            _clusters[cluster.Id] = cluster;
        }

        foreach (var id in changes.RemovedItemIds)
        {
            if (_items.Remove(id, out var removed))
            {
                Unindex(removed);
            }
        }

        foreach (var item in changes.Items)
        {
            if (_items.TryGetValue(item.Id, out var previous))
            {
                Unindex(previous);
            }

            _items[item.Id] = item;
            _itemIdBySequence[item.Sequence] = item.Id;
            if (item.State == ItemState.Placed && item.ClusterId is { } clusterId)
            {
                if (!_membersByCluster.TryGetValue(clusterId, out var members))
                {
                    _membersByCluster[clusterId] = members = [];
                }

                members.Add(item.Sequence);
            }
            else if (item.State == ItemState.Waiting)
            {
                _waiting.Add(item.Sequence);
            }
        }

        foreach (var question in changes.Questions)
        {
            _questions[question.Question.Id] = question;
            if (question.State == QuestionState.Open)
            {
                _openQuestions.Add(question.Question.Id);
            }
            else
            {
                _openQuestions.Remove(question.Question.Id);
            }
        }

        foreach (var check in changes.MergeChecks)
        {
            _mergeChecks[MergeCheck.Key(check.ClusterId, check.OtherClusterId)] = check;
        }

        _info = changes.Info;
    }

    private void Unindex(StoredItem item)
    {
        _itemIdBySequence.Remove(item.Sequence);
        _waiting.Remove(item.Sequence);
        if (item.ClusterId is { } clusterId && _membersByCluster.TryGetValue(clusterId, out var members))
        {
            members.Remove(item.Sequence);
        }
    }
}
