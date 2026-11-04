namespace Akinly;

/// <summary>A cluster formed in one call, before the caller gives it an id.</summary>
/// <param name="Centroid">The L2-normalized mean of the members' embeddings.</param>
/// <param name="ItemIds">The members, in the order they joined.</param>
public sealed record NewCluster(float[] Centroid, IReadOnlyList<long> ItemIds);
