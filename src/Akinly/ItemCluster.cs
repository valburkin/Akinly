namespace Akinly;

/// <summary>One cluster found by an <see cref="IClusterer"/>.</summary>
/// <typeparam name="TId">The type of the members' ids.</typeparam>
/// <param name="Id">The cluster's id, dense and zero-based within one call.</param>
/// <param name="Centroid">The L2-normalized mean of the members' embeddings.</param>
/// <param name="ItemIds">The members, in the order they joined.</param>
/// <param name="Sample">
/// Up to five members nearest the centroid, nearest first, then up to five farthest from it,
/// farthest first; no member twice.
/// </param>
public sealed record ItemCluster<TId>(
    int Id,
    float[] Centroid,
    IReadOnlyList<TId> ItemIds,
    IReadOnlyList<TId> Sample)
    where TId : notnull;
