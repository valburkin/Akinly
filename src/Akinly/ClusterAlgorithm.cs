namespace Akinly;

/// <summary>The clustering algorithms an <see cref="IClustererFactory"/> can resolve.</summary>
public enum ClusterAlgorithm
{
    /// <summary>The <see cref="LeaderCosineClusterer"/>, the default: seeds from the extremes first.</summary>
    Leader = 0,

    /// <summary>The <see cref="GreedyCosineClusterer"/>: one pass in input order.</summary>
    Greedy = 1,
}
