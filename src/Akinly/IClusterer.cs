namespace Akinly;

/// <summary>Groups embedded items into clusters by cosine similarity.</summary>
public interface IClusterer
{
    /// <summary>
    /// Clusters <paramref name="items"/> and returns the clusters that have at least
    /// <paramref name="minClusterSize"/> members. Cluster ids are dense, zero-based and
    /// stable within one call.
    /// </summary>
    /// <typeparam name="TId">
    /// The type of the items' ids: any type with value equality, such as a number, a string or
    /// a record struct that tells kinds of items apart (posts and their comments, say).
    /// </typeparam>
    /// <param name="items">The items to cluster, each with its embedding.</param>
    /// <param name="minClusterSize">The fewest members a returned cluster has.</param>
    /// <param name="similarityThreshold">
    /// The cosine similarity to a cluster's centroid at or above which an item may join it.
    /// </param>
    IReadOnlyList<ItemCluster<TId>> Cluster<TId>(
        IReadOnlyList<(TId ItemId, float[] Embedding)> items,
        int minClusterSize,
        double similarityThreshold = 0.85)
        where TId : notnull;
}
