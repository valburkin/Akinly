namespace Akinly.Storage;

/// <summary>A cluster near a vector.</summary>
/// <param name="Cluster">The cluster.</param>
/// <param name="Similarity">The cosine similarity of the vector and the cluster's centroid.</param>
public readonly record struct ClusterNeighbor(StoredCluster Cluster, double Similarity);
