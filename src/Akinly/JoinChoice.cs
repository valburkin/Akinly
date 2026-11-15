namespace Akinly;

/// <summary>
/// Another cluster a join question offers the item, besides <see cref="JoinQuestion.ClusterId"/>:
/// the judge picks the one cluster the item belongs in, or none.
/// </summary>
/// <param name="ClusterId">The cluster.</param>
/// <param name="Similarity">The item's similarity to the cluster's centroid.</param>
/// <param name="ClusterLabel">The cluster's label, or null.</param>
/// <param name="ExampleItemIds">Members nearest the cluster's centroid, to show the judge.</param>
public sealed record JoinChoice(long ClusterId, double Similarity, string? ClusterLabel, IReadOnlyList<string> ExampleItemIds);
