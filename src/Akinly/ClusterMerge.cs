namespace Akinly;

/// <summary>
/// Two clusters that became one. The id of <see cref="FromId"/> now redirects to
/// <see cref="IntoId"/>: update links and stored references to it.
/// </summary>
/// <param name="FromId">The cluster that went away.</param>
/// <param name="IntoId">The cluster that took its members and kept its id.</param>
/// <param name="Reason">Similarity alone, or a judge's answer.</param>
public readonly record struct ClusterMerge(long FromId, long IntoId, PlacementReason Reason);
