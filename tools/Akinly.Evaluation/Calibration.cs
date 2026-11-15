namespace Akinly.Evaluation;

/// <summary>Suggested thresholds for an embedding model, with the evidence behind them.</summary>
/// <param name="Dataset">The data they were measured on.</param>
/// <param name="Embedder">The embedding model, as provider:model.</param>
/// <param name="Items">How many items were replayed.</param>
/// <param name="Groups">How many groups they belong to.</param>
/// <param name="JoinSimilarity">Suggested <see cref="ClusteringOptions.JoinSimilarity"/>.</param>
/// <param name="CheckSimilarity">Suggested <see cref="ClusteringOptions.CheckSimilarity"/>.</param>
/// <param name="MergeSimilarity">Suggested <see cref="ClusteringOptions.MergeSimilarity"/>.</param>
/// <param name="MergeCheckSimilarity">Suggested <see cref="ClusteringOptions.MergeCheckSimilarity"/>.</param>
/// <param name="JoinPrecision">At the join threshold, how often the nearest cluster was the item's own; null if no candidate was that close.</param>
/// <param name="JoinRecall">Of items whose group already had a cluster, the share joined without a question.</param>
/// <param name="CheckRecall">Of items whose group already had a cluster, the share a judge would be asked about: their own cluster among the nearest a judge sees, and at or above the check threshold.</param>
/// <param name="MergePrecision">At or above the merge threshold, how often a pair of parts was of one group; null if no pair was that close.</param>
/// <param name="MergeCheckRecall">The share of groups cut in two whose parts are among each other's nearest candidates and at or above the merge check threshold.</param>
/// <param name="JoinBins">Nearest-cluster similarity against how often it was the item's own cluster.</param>
/// <param name="MergeBins">The similarity of pairs of parts against how often they were of one group.</param>
/// <param name="Warnings">What a reader should know about these numbers.</param>
/// <param name="Precision">The thresholds that prefer precision: joins and merges right 98% of the time, the same ask zones.</param>
/// <param name="Recall">The thresholds that prefer recall: joins and merges right 90% of the time, the same ask zones.</param>
public sealed record Calibration(
    string Dataset, string Embedder, int Items, int Groups,
    double JoinSimilarity, double CheckSimilarity, double MergeSimilarity, double MergeCheckSimilarity,
    double? JoinPrecision, double JoinRecall, double CheckRecall, double? MergePrecision, double MergeCheckRecall,
    IReadOnlyList<Bin> JoinBins, IReadOnlyList<Bin> MergeBins, IReadOnlyList<string> Warnings,
    ThresholdSet Precision, ThresholdSet Recall);
