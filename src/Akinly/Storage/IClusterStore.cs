namespace Akinly.Storage;

/// <summary>
/// Where a <see cref="Clusterer"/> keeps its clusters, items and questions. The clusterer reads
/// what it needs, computes a <see cref="ChangeSet"/>, and writes it with one
/// <see cref="ApplyAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// Reads return committed state. Live clusters are those not merged into another
/// (<see cref="StoredCluster.IsLive"/>). Results must be deterministic: order ties by id.
/// </para>
/// <para>
/// A store that can compare and swap its version makes <see cref="ApplyAsync"/> an optimistic
/// write, safe with several writers; one that cannot must document that it supports one
/// writer at a time.
/// </para>
/// </remarks>
public interface IClusterStore
{
    /// <summary>The store's metadata.</summary>
    Task<StoreInfo> GetInfoAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The live clusters nearest a vector by cosine similarity to their centroids, most similar
    /// first (ties by id), at most <paramref name="count"/>.
    /// </summary>
    /// <param name="vector">A unit-length vector.</param>
    /// <param name="count">How many to return at most.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    Task<IReadOnlyList<ClusterNeighbor>> NearestClustersAsync(ReadOnlyMemory<float> vector, int count, CancellationToken cancellationToken);

    /// <summary>The clusters with these ids, live or merged; ids not found are left out.</summary>
    Task<IReadOnlyList<StoredCluster>> GetClustersAsync(IEnumerable<long> ids, CancellationToken cancellationToken);

    /// <summary>Every live cluster, by id.</summary>
    IAsyncEnumerable<StoredCluster> ListClustersAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The live clusters that changed since the last merge check, with ids above
    /// <paramref name="afterId"/>, by id, at most <paramref name="count"/>.
    /// </summary>
    Task<IReadOnlyList<StoredCluster>> GetChangedClustersAsync(long afterId, int count, CancellationToken cancellationToken);

    /// <summary>The items with these ids; ids not found are left out.</summary>
    Task<IReadOnlyList<StoredItem>> GetItemsAsync(IEnumerable<string> ids, CancellationToken cancellationToken);

    /// <summary>The members of a cluster, by sequence.</summary>
    Task<IReadOnlyList<StoredItem>> GetMembersAsync(long clusterId, CancellationToken cancellationToken);

    /// <summary>
    /// The ids of a cluster's members nearest its centroid, nearest first (ties by sequence),
    /// at most <paramref name="count"/>.
    /// </summary>
    Task<IReadOnlyList<string>> GetExamplesAsync(long clusterId, int count, CancellationToken cancellationToken);

    /// <summary>The items waiting to ask their question, by sequence, at most <paramref name="count"/>.</summary>
    Task<IReadOnlyList<StoredItem>> GetWaitingItemsAsync(int count, CancellationToken cancellationToken);

    /// <summary>The questions with these ids; ids not found are left out.</summary>
    Task<IReadOnlyList<StoredQuestion>> GetQuestionsAsync(IEnumerable<QuestionId> ids, CancellationToken cancellationToken);

    /// <summary>The open questions, by id.</summary>
    Task<IReadOnlyList<StoredQuestion>> GetOpenQuestionsAsync(CancellationToken cancellationToken);

    /// <summary>The merge checks recorded for these pairs (each with the lower id first); pairs never checked are left out.</summary>
    Task<IReadOnlyList<MergeCheck>> GetMergeChecksAsync(IEnumerable<(long ClusterId, long OtherClusterId)> pairs, CancellationToken cancellationToken);

    /// <summary>Writes a change set as one unit.</summary>
    /// <param name="changes">The changes; <c>changes.Info.Version</c> is <paramref name="expectedVersion"/> + 1.</param>
    /// <param name="expectedVersion">The version the change set was computed from.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <exception cref="ClusterStoreConflictException">The store is no longer at <paramref name="expectedVersion"/>.</exception>
    Task ApplyAsync(ChangeSet changes, long expectedVersion, CancellationToken cancellationToken);
}
