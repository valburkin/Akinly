namespace Akinly.Evaluation;

/// <summary>The four thresholds for one preference, as <see cref="ClusteringOptions"/> names them.</summary>
public sealed record ThresholdSet(double JoinSimilarity, double CheckSimilarity, double MergeSimilarity, double MergeCheckSimilarity);
