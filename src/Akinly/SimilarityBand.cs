namespace Akinly;

/// <summary>The questions a judge answered in one band of similarity, and how many it answered yes.</summary>
/// <param name="From">The band's lowest similarity, inclusive.</param>
/// <param name="To">The band's highest similarity, exclusive.</param>
/// <param name="Questions">The questions answered yes or no in the band; failed answers are not counted.</param>
/// <param name="Yes">The questions answered yes (a join to any cluster offered, or a merge).</param>
public sealed record SimilarityBand(double From, double To, int Questions, int Yes)
{
    /// <summary>The share answered yes, or 0 when the band has no questions.</summary>
    public double YesRate => Questions == 0 ? 0 : (double)Yes / Questions;
}
