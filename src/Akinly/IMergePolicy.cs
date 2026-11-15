namespace Akinly;

/// <summary>
/// Your rules for merges: which clusters must never become one, and which keeps its id when
/// two do. Consulted before every merge, certain or judged, and before a merge question is
/// asked, so a pair that may never merge costs no judge's answer.
/// </summary>
/// <remarks>
/// Use it for what only you know about clusters: they belong to different tenants or
/// languages, a person kept two apart, or one has a public address that should outlive the
/// other. Look your own data up by the clusters' ids. It is called from the clusterer's work,
/// possibly several times for one pair and from several threads, so keep it fast and
/// side-effect free; an exception it throws fails the call that asked.
/// </remarks>
public interface IMergePolicy
{
    /// <summary>Decides whether two live clusters may merge, and which keeps its id.</summary>
    /// <param name="first">The cluster with the lower id.</param>
    /// <param name="second">The cluster with the higher id.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The rule for this pair.</returns>
    ValueTask<MergeRule> DecideAsync(Cluster first, Cluster second, CancellationToken cancellationToken);
}
