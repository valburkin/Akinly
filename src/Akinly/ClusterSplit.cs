namespace Akinly;

/// <summary>
/// A loose cluster split into parts (cosine-only setups). The largest part keeps the cluster's
/// id; the others are new clusters, and their members moved to them.
/// </summary>
/// <param name="ClusterId">The cluster that split; it keeps the largest part.</param>
/// <param name="NewClusterIds">The new clusters made from the other parts.</param>
public sealed record ClusterSplit(long ClusterId, IReadOnlyList<long> NewClusterIds);
