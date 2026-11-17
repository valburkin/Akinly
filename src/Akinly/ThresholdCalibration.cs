namespace Akinly;

/// <summary>
/// What <see cref="ThresholdCalibrator"/> found: how often a judge said yes at each similarity,
/// and the ask-zone floors that follow for this data.
/// </summary>
public sealed class ThresholdCalibration
{
    internal ThresholdCalibration(
        IReadOnlyList<SimilarityBand> joinBands, IReadOnlyList<SimilarityBand> mergeBands, double checkSimilarity, double mergeCheckSimilarity,
        IReadOnlyList<string> warnings)
    {
        JoinBands = joinBands;
        MergeBands = mergeBands;
        CheckSimilarity = checkSimilarity;
        MergeCheckSimilarity = mergeCheckSimilarity;
        Warnings = warnings;
    }

    /// <summary>The judge's answers to each item's first join question, by the similarity of the nearest cluster offered.</summary>
    public IReadOnlyList<SimilarityBand> JoinBands { get; }

    /// <summary>The judge's answers to merge questions, by the similarity of the two clusters, measured as <see cref="ClusteringOptions.MergeLinkage"/> says.</summary>
    public IReadOnlyList<SimilarityBand> MergeBands { get; }

    /// <summary>The suggested <see cref="ClusteringOptions.CheckSimilarity"/>.</summary>
    public double CheckSimilarity { get; }

    /// <summary>The suggested <see cref="ClusteringOptions.MergeCheckSimilarity"/>.</summary>
    public double MergeCheckSimilarity { get; }

    /// <summary>What to look at before trusting the suggestion, one sentence each, such as too few questions.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>A copy of the options with the suggested floors.</summary>
    /// <param name="options">The options to start from; they are not changed.</param>
    /// <returns>The copy, with <see cref="ClusteringOptions.CheckSimilarity"/> and <see cref="ClusteringOptions.MergeCheckSimilarity"/> set.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public ClusteringOptions ApplyTo(ClusteringOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var applied = options.Clone();
        applied.CheckSimilarity = Math.Min(CheckSimilarity, applied.JoinSimilarity);
        applied.MergeCheckSimilarity = Math.Min(MergeCheckSimilarity, applied.MergeSimilarity);
        return applied;
    }
}
