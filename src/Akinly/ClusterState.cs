namespace Akinly;

/// <summary>A cluster as incremental clustering sees it: a lasting id, its centroid and its size.</summary>
/// <param name="Id">The cluster's lasting id, assigned by the caller.</param>
/// <param name="Centroid">The L2-normalized mean of the members' embeddings.</param>
/// <param name="Count">How many members the cluster has.</param>
public sealed record ClusterState(long Id, float[] Centroid, int Count);
