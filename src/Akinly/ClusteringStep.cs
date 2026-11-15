namespace Akinly;

/// <summary>What one call to the clusterer changed, and what it still needs answered.</summary>
/// <param name="Placements">Items placed in this step, in order.</param>
/// <param name="Merges">Clusters merged in this step, in order.</param>
/// <param name="Splits">Clusters split in this step, in order.</param>
/// <param name="Questions">Questions asked in this step, to put to a judge.</param>
public sealed record ClusteringStep(
    IReadOnlyList<Placement> Placements,
    IReadOnlyList<ClusterMerge> Merges,
    IReadOnlyList<ClusterSplit> Splits,
    IReadOnlyList<Question> Questions)
{
    /// <summary>A step that changed nothing.</summary>
    public static ClusteringStep Empty { get; } = new([], [], [], []);

    /// <summary>This step followed by <paramref name="next"/>, as one.</summary>
    /// <param name="next">The later step.</param>
    public ClusteringStep Then(ClusteringStep next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return new ClusteringStep(
            [.. Placements, .. next.Placements],
            [.. Merges, .. next.Merges],
            [.. Splits, .. next.Splits],
            [.. Questions, .. next.Questions]);
    }
}
