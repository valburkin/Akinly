using System.Runtime.CompilerServices;
using Akinly.Storage;
using static Akinly.Tests.TestVectors;

namespace Akinly.Tests;

/// <summary>How the clusterer copes with its store: conflicts, and sums kept honest.</summary>
public sealed class ClustererStoreBehaviorTests
{
    [Fact]
    public async Task A_conflicting_write_is_recomputed_and_retried()
    {
        var store = new ConflictingStore(new InMemoryClusterStore(), conflicts: 2);
        var clusterer = new Clusterer(store, CosineOnly());

        await clusterer.AddAsync([Item("a", At(0))]);

        Assert.Equal(3, store.Attempts);
        Assert.Equal(1L, await clusterer.GetClusterOfAsync("a"));
    }

    [Fact]
    public async Task After_sixteen_conflicts_the_clusterer_gives_up()
    {
        var store = new ConflictingStore(new InMemoryClusterStore(), conflicts: int.MaxValue);
        var clusterer = new Clusterer(store, CosineOnly());

        await Assert.ThrowsAsync<ClusterStoreConflictException>(() => clusterer.AddAsync([Item("a", At(0))]));

        Assert.Equal(16, store.Attempts);
    }

    [Fact]
    public async Task A_call_that_changes_nothing_writes_nothing()
    {
        var store = new ConflictingStore(new InMemoryClusterStore(), conflicts: 0);
        var clusterer = new Clusterer(store, CosineOnly());

        await clusterer.AddAsync([]);
        await clusterer.MaintainAsync();
        await clusterer.AnswerAsync([]);
        await clusterer.RemoveAsync(["nobody"]);

        Assert.Equal(0, store.Attempts);
    }

    [Fact]
    public async Task After_many_removals_maintenance_recomputes_the_sum_from_the_members()
    {
        var store = new InMemoryClusterStore();
        var clusterer = new Clusterer(store, CosineOnly(o => o.JoinSimilarity = o.CheckSimilarity = 0.5));
        await clusterer.AddAsync([.. Enumerable.Range(0, 20).Select(i => Item($"i{i}", At(i)))]);
        await clusterer.RemoveAsync([.. Enumerable.Range(0, 17).Select(i => $"i{i}")]);
        Assert.Equal(17, (await store.GetClustersAsync([1], default))[0].RemovalsSinceRecompute);

        await clusterer.MaintainAsync();

        var cluster = (await store.GetClustersAsync([1], default))[0];
        Assert.Equal(0, cluster.RemovalsSinceRecompute);
        Assert.Equal(3, cluster.Count);
        Assert.Equal(1.0, Vectors.Dot(cluster.Centroid.ToArray(), At(18)), 5);
    }

    [Fact]
    public async Task Clusters_started_in_the_same_call_do_not_widen_the_store_search()
    {
        var store = new ConflictingStore(new InMemoryClusterStore(), conflicts: 0);
        var clusterer = new Clusterer(store, CosineOnly(o => o.JoinSimilarity = o.CheckSimilarity = 0.9999));

        // Fifty items, each far enough from the others to start a cluster of its own.
        await clusterer.AddAsync([.. Enumerable.Range(0, 50).Select(i => Item($"i{i}", At(i * 3)))]);

        Assert.Equal(50, (await store.GetInfoAsync(default)).NextClusterId - 1);
        Assert.True(store.LargestSearch < 20, $"asked the store for {store.LargestSearch} clusters");
    }

    [Fact]
    public async Task Lookups_on_unknown_ids_return_nothing()
    {
        var clusterer = new Clusterer(new InMemoryClusterStore(), CosineOnly());

        Assert.Null(await clusterer.GetClusterAsync(404));
        Assert.Empty(await clusterer.GetMembersAsync(404));
        Assert.Null(await clusterer.GetClusterOfAsync("nobody"));
    }

    /// <summary>A store that refuses its first writes as conflicts, as if another writer came first, and records its largest search.</summary>
    private sealed class ConflictingStore(IClusterStore inner, int conflicts) : IClusterStore
    {
        private int _remaining = conflicts;

        public int Attempts { get; private set; }

        public int LargestSearch { get; private set; }

        public Task ApplyAsync(ChangeSet changes, long expectedVersion, CancellationToken cancellationToken)
        {
            Attempts++;
            if (_remaining > 0)
            {
                _remaining--;
                throw new ClusterStoreConflictException(expectedVersion, expectedVersion + 1);
            }

            return inner.ApplyAsync(changes, expectedVersion, cancellationToken);
        }

        public Task<StoreInfo> GetInfoAsync(CancellationToken cancellationToken) => inner.GetInfoAsync(cancellationToken);

        public Task<IReadOnlyList<ClusterNeighbor>> NearestClustersAsync(ReadOnlyMemory<float> vector, int count, CancellationToken cancellationToken)
        {
            LargestSearch = Math.Max(LargestSearch, count);
            return inner.NearestClustersAsync(vector, count, cancellationToken);
        }

        public Task<IReadOnlyList<StoredCluster>> GetClustersAsync(IEnumerable<long> ids, CancellationToken cancellationToken) =>
            inner.GetClustersAsync(ids, cancellationToken);

        public async IAsyncEnumerable<StoredCluster> ListClustersAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var cluster in inner.ListClustersAsync(cancellationToken))
            {
                yield return cluster;
            }
        }

        public Task<IReadOnlyList<StoredCluster>> GetChangedClustersAsync(long afterId, int count, CancellationToken cancellationToken) =>
            inner.GetChangedClustersAsync(afterId, count, cancellationToken);

        public Task<IReadOnlyList<StoredItem>> GetItemsAsync(IEnumerable<string> ids, CancellationToken cancellationToken) =>
            inner.GetItemsAsync(ids, cancellationToken);

        public Task<IReadOnlyList<StoredItem>> GetMembersAsync(long clusterId, CancellationToken cancellationToken) =>
            inner.GetMembersAsync(clusterId, cancellationToken);

        public Task<IReadOnlyList<string>> GetExamplesAsync(long clusterId, int count, CancellationToken cancellationToken) =>
            inner.GetExamplesAsync(clusterId, count, cancellationToken);

        public Task<IReadOnlyList<StoredItem>> GetWaitingItemsAsync(int count, CancellationToken cancellationToken) =>
            inner.GetWaitingItemsAsync(count, cancellationToken);

        public Task<IReadOnlyList<StoredQuestion>> GetQuestionsAsync(IEnumerable<QuestionId> ids, CancellationToken cancellationToken) =>
            inner.GetQuestionsAsync(ids, cancellationToken);

        public Task<IReadOnlyList<StoredQuestion>> GetOpenQuestionsAsync(CancellationToken cancellationToken) =>
            inner.GetOpenQuestionsAsync(cancellationToken);

        public Task<IReadOnlyList<MergeCheck>> GetMergeChecksAsync(IEnumerable<(long ClusterId, long OtherClusterId)> pairs, CancellationToken cancellationToken) =>
            inner.GetMergeChecksAsync(pairs, cancellationToken);
    }
}
