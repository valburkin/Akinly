namespace Akinly;

/// <summary>A cluster as incremental clustering sees it: a lasting id, its centroid and its size.</summary>
/// <param name="Id">The cluster's lasting id, assigned by the caller.</param>
/// <param name="Centroid">
/// The mean direction of the members' embeddings. The clusterer returns it unit length, and
/// normalizes one passed in that is not.
/// </param>
/// <param name="Count">How many members the cluster has: zero or more.</param>
public sealed record ClusterState(long Id, float[] Centroid, int Count);
