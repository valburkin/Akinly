namespace Akinly;

/// <summary>How <see cref="ThresholdCalibrator"/> explores the ask zones and where it sets their floors.</summary>
public sealed class CalibrationOptions
{
    /// <summary>
    /// The lowest similarity at which the trial run asks join questions. Default 0.30, far below
    /// any useful floor for <c>text-embedding-3-small</c>; lower it for a model whose
    /// similarities run lower.
    /// </summary>
    public double LowestCheckSimilarity { get; set; } = 0.30;

    /// <summary>
    /// The lowest similarity at which the trial run asks merge questions, measured as the
    /// options' <see cref="ClusteringOptions.MergeLinkage"/> says. Default 0.30.
    /// </summary>
    public double LowestMergeCheckSimilarity { get; set; } = 0.30;

    /// <summary>
    /// The share of join questions a judge should answer yes for the zone to be worth asking.
    /// The check floor is set where the judge's yes-rate on first questions rises to it. Default
    /// 0.30: a zone where the judge says yes less often costs many questions for few joins.
    /// </summary>
    public double JoinYesRate { get; set; } = 0.30;

    /// <summary>
    /// The share of merge questions a judge should answer yes for the zone to be worth asking.
    /// Default 0.08: merge questions are fewer, and a missed merge leaves a duplicate cluster.
    /// </summary>
    public double MergeYesRate { get; set; } = 0.08;

    /// <summary>The width of a similarity band. Default 0.02.</summary>
    public double BandWidth { get; set; } = 0.02;

    /// <summary>Items added per call in the trial run. Default 50.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>The problems with these options, one sentence each; empty when they are valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        Share(LowestCheckSimilarity, nameof(LowestCheckSimilarity), errors);
        Share(LowestMergeCheckSimilarity, nameof(LowestMergeCheckSimilarity), errors);
        Share(JoinYesRate, nameof(JoinYesRate), errors);
        Share(MergeYesRate, nameof(MergeYesRate), errors);
        if (!double.IsFinite(BandWidth) || BandWidth < 0.005 || BandWidth > 0.2)
        {
            errors.Add("BandWidth must be from 0.005 to 0.2.");
        }

        if (BatchSize < 1)
        {
            errors.Add("BatchSize must be at least 1.");
        }

        return errors;
    }

    private static void Share(double value, string name, List<string> errors)
    {
        if (!double.IsFinite(value) || value <= 0 || value > 1)
        {
            errors.Add($"{name} must be above 0 and at most 1.");
        }
    }
}
