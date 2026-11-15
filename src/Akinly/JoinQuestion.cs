namespace Akinly;

/// <summary>Does an item belong in a cluster?</summary>
/// <param name="Id">The question's id.</param>
/// <param name="ItemId">The item to place.</param>
/// <param name="ClusterId">The cluster it might join: its nearest that has not refused it.</param>
/// <param name="Similarity">The item's cosine similarity to the cluster's centroid.</param>
/// <param name="ClusterLabel">The cluster's label, when it has one: what a reader would see.</param>
/// <param name="ExampleItemIds">Members of the cluster nearest its centroid, nearest first, to show the judge.</param>
public sealed record JoinQuestion(
    QuestionId Id,
    string ItemId,
    long ClusterId,
    double Similarity,
    string? ClusterLabel,
    IReadOnlyList<string> ExampleItemIds)
    : Question(Id, Similarity)
{
    /// <summary>
    /// Other clusters offered with <see cref="ClusterId"/>, nearest first; empty for a yes-or-no
    /// question. With choices, the judge answers with the cluster the item belongs in
    /// (<see cref="Verdict.Picked"/>), or <see cref="VerdictKind.Different"/> for none of them.
    /// </summary>
    public IReadOnlyList<JoinChoice> OtherChoices { get; init; } = [];
}
