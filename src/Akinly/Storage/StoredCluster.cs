namespace Akinly.Storage;

/// <summary>A cluster as a store keeps it.</summary>
/// <param name="Id">The cluster's lasting id.</param>
/// <param name="Centroid">
/// The unit-length mean direction of its members, derived from <paramref name="Sum"/>; for a
/// cluster with no members left, the last centroid it had. Stores index it for nearest-cluster search.
/// </param>
/// <param name="Sum">The sum of its members' unit-length embeddings, in double precision.</param>
/// <param name="Count">How many members it has.</param>
/// <param name="Label">Its label, when it has one.</param>
/// <param name="MergedIntoId">The cluster it was merged into, or null while it is live.</param>
/// <param name="Changed">Whether it changed since the last merge check.</param>
/// <param name="RemovalsSinceRecompute">Members removed since its sum was last recomputed from its members.</param>
/// <param name="RefusedBy">
/// The clusters that refused one of its members. A merge with one of them is skipped when
/// <see cref="ClusteringOptions.MergeRespectsRefusals"/> is on.
/// </param>
/// <param name="LabeledAtCount">Its size when its label was set, or 0 when it has none: a labeler relabels it once it has grown.</param>
public sealed record StoredCluster(
    long Id,
    ReadOnlyMemory<float> Centroid,
    ReadOnlyMemory<double> Sum,
    int Count,
    string? Label,
    long? MergedIntoId,
    bool Changed,
    int RemovalsSinceRecompute,
    IReadOnlyList<long> RefusedBy,
    int LabeledAtCount = 0)
{
    /// <summary>Whether the cluster is live: not merged into another.</summary>
    public bool IsLive => MergedIntoId is null;

    /// <summary>The cluster as a reader sees it.</summary>
    public Cluster ToCluster() => new(Id, Count, Label, Centroid, LabeledAtCount);
}
