namespace Akinly;

/// <summary>
/// How a refusal bears on merging two clusters: one cluster refused an item that is now in the
/// other, so merging them would carry the item into a cluster that said no to it.
/// </summary>
public enum RefusalRule
{
    /// <summary>
    /// A judge decides: the pair is never merged on similarity alone, and is asked about from
    /// <see cref="ClusteringOptions.MergeCheckSimilarity"/> up. One item's refusal says little
    /// about whether two whole clusters are the same, so it should not settle the pair by itself.
    /// With no merge ask zone (cosine-only merges) there is no one to ask, and it works as
    /// <see cref="Block"/>. The default.
    /// </summary>
    Ask = 0,

    /// <summary>The pair is never merged, whatever its similarity or a judge's answer.</summary>
    Block = 1,

    /// <summary>The refusal is not considered: the pair merges like any other.</summary>
    Ignore = 2,
}
