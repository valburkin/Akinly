namespace Akinly.Storage;

/// <summary>
/// A pair of clusters a judge was asked about, so the pair is not asked about again. The pair
/// is stored with the lower id first.
/// </summary>
/// <param name="ClusterId">The cluster with the lower id.</param>
/// <param name="OtherClusterId">The cluster with the higher id.</param>
/// <param name="Outcome">
/// The answer that settled the pair: <see cref="VerdictKind.Same"/>, <see cref="VerdictKind.Different"/>
/// (also recorded for the parts of a split), or <see cref="VerdictKind.Unanswerable"/>. A pair the
/// judge failed to answer is not recorded, so it is asked again once either cluster changes.
/// </param>
public sealed record MergeCheck(long ClusterId, long OtherClusterId, VerdictKind Outcome)
{
    /// <summary>The key of a pair: the lower id first.</summary>
    public static (long, long) Key(long a, long b) => a < b ? (a, b) : (b, a);
}
