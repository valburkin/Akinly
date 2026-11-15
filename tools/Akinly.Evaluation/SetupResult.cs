namespace Akinly.Evaluation;

/// <summary>One setup's results over several input orders.</summary>
/// <param name="Setup">What ran.</param>
/// <param name="Settings">Its thresholds.</param>
/// <param name="Runs">The scores of each input order.</param>
/// <param name="SecondsPerRun">Wall-clock time of one run, on average.</param>
/// <param name="Judge">For a judged setup, what the judge did; null otherwise.</param>
public sealed record SetupResult(string Setup, string Settings, IReadOnlyList<Scores> Runs, double SecondsPerRun, JudgeUsage? Judge = null)
{
    public double Mean(Func<Scores, double> measure) => Runs.Average(measure);

    /// <summary>The sample standard deviation over the runs; 0 for one run.</summary>
    public double Spread(Func<Scores, double> measure)
    {
        var mean = Mean(measure);
        return Runs.Count < 2 ? 0 : Math.Sqrt(Runs.Sum(r => Math.Pow(measure(r) - mean, 2)) / (Runs.Count - 1));
    }
}
