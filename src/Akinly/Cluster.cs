namespace Akinly;

/// <summary>A cluster as a reader sees it.</summary>
/// <param name="Id">The cluster's lasting id.</param>
/// <param name="Count">How many items it holds.</param>
/// <param name="Label">Its label (a one-sentence title), when it has one.</param>
/// <param name="Centroid">The mean direction of its members' embeddings, unit length.</param>
/// <param name="LabeledAtCount">Its size when its label was set, or 0 when it has none.</param>
public sealed record Cluster(long Id, int Count, string? Label, ReadOnlyMemory<float> Centroid, int LabeledAtCount = 0);
