namespace Akinly;

/// <summary>A judge that answers each question with a function: for rules, tests, or a person in the loop.</summary>
/// <remarks>
/// A function that returns a <see cref="VerdictKind"/> answers about the nearest cluster only: its
/// <see cref="VerdictKind.Different"/> refuses every cluster a join question offers. Return a
/// <see cref="Verdict"/> to pick another of them (<see cref="Verdict.Picked"/>), or run with
/// <see cref="ClusteringOptions.JoinChoices"/> = 1. An exception the function throws becomes a
/// <see cref="VerdictKind.Failed"/> verdict, never a refusal.
/// </remarks>
public sealed class DelegateJudge : IClusterJudge
{
    private readonly Func<Question, CancellationToken, ValueTask<Verdict>> _answer;

    /// <summary>A judge that answers each question with an asynchronous function.</summary>
    /// <param name="answer">Answers one question.</param>
    public DelegateJudge(Func<Question, CancellationToken, ValueTask<VerdictKind>> answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        _answer = async (question, cancellationToken) =>
            new Verdict(question.Id, await answer(question, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>A judge that answers each question with a synchronous function.</summary>
    /// <param name="answer">Answers one question.</param>
    public DelegateJudge(Func<Question, VerdictKind> answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        _answer = (question, _) => ValueTask.FromResult(new Verdict(question.Id, answer(question)));
    }

    /// <summary>
    /// A judge that answers each question with an asynchronous function returning a whole verdict,
    /// which can pick any cluster a join question offers.
    /// </summary>
    /// <param name="answer">Answers one question; the verdict's question id must be the question's.</param>
    public DelegateJudge(Func<Question, CancellationToken, ValueTask<Verdict>> answer)
    {
        _answer = answer ?? throw new ArgumentNullException(nameof(answer));
    }

    /// <summary>
    /// A judge that answers each question with a synchronous function returning a whole verdict,
    /// which can pick any cluster a join question offers.
    /// </summary>
    /// <param name="answer">Answers one question; the verdict's question id must be the question's.</param>
    public DelegateJudge(Func<Question, Verdict> answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        _answer = (question, _) => ValueTask.FromResult(answer(question));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Verdict>> JudgeAsync(IReadOnlyList<Question> questions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(questions);
        var verdicts = new List<Verdict>(questions.Count);
        foreach (var question in questions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            verdicts.Add(await AnswerAsync(question, cancellationToken).ConfigureAwait(false));
        }

        return verdicts;
    }

    private async Task<Verdict> AnswerAsync(Question question, CancellationToken cancellationToken)
    {
        try
        {
            var verdict = await _answer(question, cancellationToken).ConfigureAwait(false);
            return verdict.QuestionId == question.Id ? verdict : Verdict.Failed(question.Id);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // A judge's failure is a Failed verdict, never an exception (IClusterJudge).
        catch (Exception)
#pragma warning restore CA1031
        {
            return Verdict.Failed(question.Id);
        }
    }
}
