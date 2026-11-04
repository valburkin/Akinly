namespace Akinly;

/// <summary>Two clusters close enough to be one.</summary>
/// <param name="FromId">The smaller cluster, which goes away.</param>
/// <param name="IntoId">The larger cluster, which keeps its id and takes the members.</param>
/// <param name="Similarity">The cosine similarity of the two centroids.</param>
public readonly record struct MergeDecision(long FromId, long IntoId, double Similarity);
