namespace Akinly;

/// <summary>
/// Splitting loose clusters by similarity, during <see cref="Clusterer.MaintainAsync"/>. Only for
/// cosine-only setups: when a judge approved each member, a similarity number should not undo
/// that, so splits are refused when the options have an ask zone.
/// </summary>
public sealed class SplitOptions
{
    /// <summary>Whether loose clusters are split. Default off.</summary>
    public bool Enabled { get; set; }

    /// <summary>The cohesion (mean similarity of members to the centroid) under which a cluster is loose. Default 0.80.</summary>
    public double CohesionThreshold { get; set; } = 0.80;

    /// <summary>The join threshold used to regroup a loose cluster's members. Default 0.85.</summary>
    public double RegroupSimilarity { get; set; } = 0.85;

    /// <summary>The fewest members a part needs to stand alone. Default 3.</summary>
    public int MinPartSize { get; set; } = 3;
}
