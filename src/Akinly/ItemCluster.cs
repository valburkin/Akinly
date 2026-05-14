namespace Akinly;

/// <summary>One cluster found by an <see cref="IClusterer"/>.</summary>
/// <param name="Id">The cluster's id, dense and zero-based within one call.</param>
/// <param name="Centroid">The L2-normalized mean of the members' embeddings.</param>
/// <param name="ItemIds">The members, in the order they joined.</param>
/// <param name="NearestToCentroid">Up to ten members closest to the centroid, closest first.</param>
public sealed record ItemCluster(
    int Id,
    float[] Centroid,
    IReadOnlyList<long> ItemIds,
    IReadOnlyList<long> NearestToCentroid);
