using Akinly.Storage;
using Microsoft.Extensions.VectorData;

namespace Akinly.VectorData;

/// <summary>
/// An <see cref="IClusterStore"/> over a vector database with a Microsoft.Extensions.VectorData
/// connector. Tested with Qdrant, PostgreSQL with pgvector and the in-memory connector; another
/// connector works if it supports filtered reads with ordering and shows each write to the next
/// read at once. Nearest clusters come from the database's own vector index.
/// </summary>
/// <remarks>
/// <para>
/// <b>One writer at a time.</b> Vector databases offer neither transactions across collections
/// nor compare-and-swap, so the version check catches another writer that came first but cannot
/// stop a race. Run the clusterer on one instance, or behind a lock of your own.
/// </para>
/// <para>
/// <b>Crash-safe for writes.</b> Each change set is checked for anything the database could
/// not store, written to a journal first, then applied with upserts, then the metadata's version
/// moves and the journal is deleted. Before its first write, and after any write that failed,
/// the store finishes a change set left in the journal. Reads do not: between a failed write and the next one, a reader can see part of a
/// change set (an item placed in a cluster whose count has not moved yet), as it can while a
/// write is under way.
/// </para>
/// <para>
/// It keeps five collections, named from <see cref="VectorDataClusterStoreOptions.CollectionPrefix"/>:
/// clusters and items (with their vectors), questions, merge checks, and metadata. Akinly's ids
/// map to the connector's keys (<see cref="VectorDataClusterStoreOptions.KeyKind"/>).
/// </para>
/// </remarks>
public sealed class VectorDataClusterStore : IClusterStore
{
    private readonly IClusterStore _inner;

    /// <summary>A store over a vector database.</summary>
    /// <param name="store">The connector's vector store, such as a <c>QdrantVectorStore</c> or a <c>PostgresVectorStore</c>.</param>
    /// <param name="options">The collections' layout; <see cref="VectorDataClusterStoreOptions.Dimensions"/> is required.</param>
    /// <exception cref="ArgumentException">The options are invalid.</exception>
    public VectorDataClusterStore(VectorStore store, VectorDataClusterStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        options = options.Clone();
        var errors = options.Validate();
        if (errors.Count > 0)
        {
            throw new ArgumentException("Invalid vector store options: " + string.Join(" ", errors), nameof(options));
        }

        _inner = options.KeyKind == VectorDataKeyKind.Text
            ? new VectorDataStore<string>(store, options, new StringKeys())
            : new VectorDataStore<Guid>(store, options, new GuidKeys());
    }

    /// <inheritdoc />
    public Task<StoreInfo> GetInfoAsync(CancellationToken cancellationToken) =>
        _inner.GetInfoAsync(cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<ClusterNeighbor>> NearestClustersAsync(ReadOnlyMemory<float> vector, int count, CancellationToken cancellationToken) =>
        _inner.NearestClustersAsync(vector, count, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<StoredCluster>> GetClustersAsync(IEnumerable<long> ids, CancellationToken cancellationToken) =>
        _inner.GetClustersAsync(ids, cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<StoredCluster> ListClustersAsync(CancellationToken cancellationToken) =>
        _inner.ListClustersAsync(cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<StoredCluster>> GetChangedClustersAsync(long afterId, int count, CancellationToken cancellationToken) =>
        _inner.GetChangedClustersAsync(afterId, count, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<StoredItem>> GetItemsAsync(IEnumerable<string> ids, CancellationToken cancellationToken) =>
        _inner.GetItemsAsync(ids, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<StoredItem>> GetMembersAsync(long clusterId, CancellationToken cancellationToken) =>
        _inner.GetMembersAsync(clusterId, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> GetExamplesAsync(long clusterId, int count, CancellationToken cancellationToken) =>
        _inner.GetExamplesAsync(clusterId, count, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<StoredItem>> GetWaitingItemsAsync(int count, CancellationToken cancellationToken) =>
        _inner.GetWaitingItemsAsync(count, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<StoredQuestion>> GetQuestionsAsync(IEnumerable<QuestionId> ids, CancellationToken cancellationToken) =>
        _inner.GetQuestionsAsync(ids, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<StoredQuestion>> GetOpenQuestionsAsync(CancellationToken cancellationToken) =>
        _inner.GetOpenQuestionsAsync(cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<MergeCheck>> GetMergeChecksAsync(IEnumerable<(long ClusterId, long OtherClusterId)> pairs, CancellationToken cancellationToken) =>
        _inner.GetMergeChecksAsync(pairs, cancellationToken);

    /// <inheritdoc />
    public Task ApplyAsync(ChangeSet changes, long expectedVersion, CancellationToken cancellationToken) =>
        _inner.ApplyAsync(changes, expectedVersion, cancellationToken);

    /// <summary>Stops writes between steps: for crash recovery tests.</summary>
    internal Func<string, Task>? OnStep
    {
        get => ((IWriteSteps)_inner).OnStep;
        set => ((IWriteSteps)_inner).OnStep = value;
    }
}
