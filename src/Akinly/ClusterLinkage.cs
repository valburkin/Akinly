namespace Akinly;

/// <summary>How the similarity of two clusters is measured when deciding whether they merge.</summary>
public enum ClusterLinkage
{
    /// <summary>
    /// The cosine similarity of their centroids, as 1.0 measured it. A centroid averages away what
    /// sets a cluster's members apart and keeps what they share, so two mixed clusters of texts
    /// written to one template can look nearly identical by their centroids though no member of
    /// one is close to any member of the other.
    /// </summary>
    Centroid = 0,

    /// <summary>
    /// The average cosine similarity of a member of one cluster to a member of the other. For two
    /// single items it is their cosine similarity; it does not rise as clusters grow mixed, so
    /// mixed clusters do not pull each other in. Computed from the clusters' stored sums, with no
    /// extra reads. The default.
    /// </summary>
    Average = 1,
}
