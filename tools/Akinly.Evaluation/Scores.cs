namespace Akinly.Evaluation;

/// <summary>How well a clustering matches the true labels.</summary>
/// <param name="AdjustedRand">Adjusted Rand index: 1 is perfect, 0 is chance.</param>
/// <param name="Accuracy">Clustering accuracy: the share of items in the cluster matched to their label, each label matched to at most one cluster (the Hungarian algorithm), as intent-discovery papers report it.</param>
/// <param name="Nmi">Normalized mutual information with the arithmetic mean, which equals V-measure.</param>
/// <param name="Homogeneity">Each cluster holds one label.</param>
/// <param name="Completeness">Each label is in one cluster.</param>
/// <param name="VMeasure">The harmonic mean of homogeneity and completeness.</param>
/// <param name="PairPrecision">Of pairs put together, the share that share a label.</param>
/// <param name="PairRecall">Of pairs that share a label, the share put together.</param>
/// <param name="PairF1">The harmonic mean of the two.</param>
/// <param name="Clusters">How many clusters.</param>
/// <param name="Singletons">How many clusters hold one item.</param>
public sealed record Scores(
    double AdjustedRand, double Accuracy, double Nmi, double Homogeneity, double Completeness, double VMeasure,
    double PairPrecision, double PairRecall, double PairF1, int Clusters, int Singletons);
