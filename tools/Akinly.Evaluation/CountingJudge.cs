namespace Akinly.Evaluation;

/// <summary>Counts a judge's verdicts by kind, so a judged run that mostly failed is not mistaken for a good one.</summary>
public sealed class CountingJudge(IClusterJudge inner) : IClusterJudge
{
    private long _verdicts;
    private long _failed;
    private long _unanswerable;

    public long Verdicts => Interlocked.Read(ref _verdicts);

    /// <summary>Failed verdicts: the model or the request failed, or its answer could not be read.</summary>
    public long Failed => Interlocked.Read(ref _failed);

    public long Unanswerable => Interlocked.Read(ref _unanswerable);

    public async Task<IReadOnlyList<Verdict>> JudgeAsync(IReadOnlyList<Question> questions, CancellationToken cancellationToken)
    {
        var verdicts = await inner.JudgeAsync(questions, cancellationToken).ConfigureAwait(false);
        Interlocked.Add(ref _verdicts, verdicts.Count);
        Interlocked.Add(ref _failed, verdicts.Count(v => v.Kind == VerdictKind.Failed) + (questions.Count - verdicts.Count));
        Interlocked.Add(ref _unanswerable, verdicts.Count(v => v.Kind == VerdictKind.Unanswerable));
        return verdicts;
    }
}
