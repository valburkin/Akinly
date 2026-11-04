namespace Akinly;

/// <summary>Where <see cref="IncrementalClusterer.Assign"/> placed each item.</summary>
/// <param name="Joined">Each item that joined an existing cluster, by item id, with that cluster's id.</param>
/// <param name="Created">The clusters formed in this call, each with the items that formed it.</param>
/// <param name="Updated">The existing clusters that took items, with their new centroids and counts, by id.</param>
public sealed record AssignmentResult(
    IReadOnlyDictionary<long, long> Joined,
    IReadOnlyList<NewCluster> Created,
    IReadOnlyList<ClusterState> Updated);
