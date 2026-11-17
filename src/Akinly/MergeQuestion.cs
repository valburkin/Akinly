namespace Akinly;

/// <summary>Are two clusters one?</summary>
/// <param name="Id">The question's id.</param>
/// <param name="ClusterId">One cluster.</param>
/// <param name="OtherClusterId">The other cluster.</param>
/// <param name="Similarity">The similarity of the two clusters, measured as <see cref="ClusteringOptions.MergeLinkage"/> says: by default, the average cosine similarity of a member of one to a member of the other.</param>
/// <param name="ClusterLabel">The first cluster's label, when it has one.</param>
/// <param name="OtherClusterLabel">The second cluster's label, when it has one.</param>
/// <param name="ExampleItemIds">Members of the first cluster nearest its centroid.</param>
/// <param name="OtherExampleItemIds">Members of the second cluster nearest its centroid.</param>
public sealed record MergeQuestion(
    QuestionId Id,
    long ClusterId,
    long OtherClusterId,
    double Similarity,
    string? ClusterLabel,
    string? OtherClusterLabel,
    IReadOnlyList<string> ExampleItemIds,
    IReadOnlyList<string> OtherExampleItemIds)
    : Question(Id, Similarity);
