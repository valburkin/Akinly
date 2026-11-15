using Akinly.Storage;
using static Akinly.Tests.TestVectors;

namespace Akinly.Tests;

public sealed class ClustererExtensionsTests
{
    [Fact]
    public async Task Settling_asks_until_no_question_is_left()
    {
        var clusterer = new Clusterer(new InMemoryClusterStore(), Judged(o => o.MaxOpenQuestions = 1));
        var asked = new List<Question>();
        var judge = new DelegateJudge(question =>
        {
            asked.Add(question);
            return question is JoinQuestion { ItemId: "accepted" } ? VerdictKind.Same : VerdictKind.Different;
        });

        var step = await clusterer.SettleAsync(
            [Item("a", At(0)), Item("refused", At(AngleFor(0.75))), Item("accepted", At(-AngleFor(0.75)))],
            judge);

        Assert.Empty(await clusterer.GetOpenQuestionsAsync());
        Assert.Equal(["refused", "accepted"], asked.OfType<JoinQuestion>().Select(q => q.ItemId));
        Assert.Contains(new Placement("refused", 2, PlacementReason.NewCluster), step.Placements);
        Assert.Contains(new Placement("accepted", 1, PlacementReason.Judge), step.Placements);
    }

    [Fact]
    public async Task A_judge_that_never_answers_still_lets_the_loop_end()
    {
        var clusterer = new Clusterer(new InMemoryClusterStore(), Judged(o => o.FailedAnswers.MaxAttempts = 3));
        var rounds = 0;
        var silent = new DelegateJudgeThatSkips(() => rounds++);

        var step = await clusterer.SettleAsync([Item("a", At(0)), Item("b", At(AngleFor(0.7)))], silent);

        Assert.Equal(3, rounds);
        Assert.Contains(new Placement("b", 2, PlacementReason.NewCluster), step.Placements);
    }

    [Fact]
    public async Task Failed_rounds_wait_before_asking_again()
    {
        var clusterer = new Clusterer(new InMemoryClusterStore(), Judged(o =>
        {
            o.FailedAnswers.MaxAttempts = 2;
            o.FailedAnswers.RetryDelay = TimeSpan.FromMilliseconds(50);
        }));
        var judge = new DelegateJudge(_ => VerdictKind.Failed);
        await clusterer.AddAsync([Item("a", At(0)), Item("b", At(AngleFor(0.7)))]);

        var started = System.Diagnostics.Stopwatch.StartNew();
        await clusterer.SettleAsync(judge);

        Assert.True(started.ElapsedMilliseconds >= 40, $"waited {started.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task A_round_with_some_failed_answers_also_waits_before_asking_them_again()
    {
        var clusterer = new Clusterer(new InMemoryClusterStore(), Judged(o =>
        {
            o.FailedAnswers.MaxAttempts = 2;
            o.FailedAnswers.RetryDelay = TimeSpan.FromMilliseconds(100);
        }));
        var judge = new DelegateJudge(q => q is JoinQuestion { ItemId: "b" } ? VerdictKind.Same : VerdictKind.Failed);
        await clusterer.AddAsync([Item("a", At(0)), Item("b", At(AngleFor(0.7))), Item("c", At(-AngleFor(0.7)))]);

        // Round 1: b is answered, c fails (wait); round 2: c fails again (wait); then similarity decides.
        var started = System.Diagnostics.Stopwatch.StartNew();
        await clusterer.SettleAsync(judge);

        Assert.True(started.ElapsedMilliseconds >= 180, $"waited {started.ElapsedMilliseconds} ms");
        Assert.Equal(1L, await clusterer.GetClusterOfAsync("b"));
    }

    [Fact]
    public void A_retry_delay_over_an_hour_is_refused()
    {
        var options = Judged(o => o.FailedAnswers.RetryDelay = TimeSpan.FromHours(2));

        Assert.Contains("FailedAnswers.RetryDelay must be from zero to one hour.", Assert.Throws<ArgumentException>(options.ThrowIfInvalid).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_delegate_judge_can_pick_any_offered_cluster_and_never_throws()
    {
        var question = new JoinQuestion(new QuestionId(1), "x", 1, 0.8, null, [])
        {
            OtherChoices = [new JoinChoice(2, 0.7, null, [])],
        };
        var picking = new DelegateJudge(q => Verdict.Picked(q.Id, 2));
        var picksAsync = new DelegateJudge((q, _) => ValueTask.FromResult(Verdict.Picked(q.Id, 2)));
        var throwing = new DelegateJudge((Func<Question, Verdict>)(_ => throw new InvalidOperationException("broken rule")));
        var wrongId = new DelegateJudge(_ => Verdict.Same(new QuestionId(99)));

        Assert.Equal(2L, (await picking.JudgeAsync([question], default))[0].PickedClusterId);
        Assert.Equal(2L, (await picksAsync.JudgeAsync([question], default))[0].PickedClusterId);
        Assert.Equal([Verdict.Failed(question.Id)], await throwing.JudgeAsync([question], default));
        Assert.Equal([Verdict.Failed(question.Id)], await wrongId.JudgeAsync([question], default));
        Assert.Throws<ArgumentNullException>(() => new DelegateJudge((Func<Question, Verdict>)null!));
        Assert.Throws<ArgumentNullException>(() => new DelegateJudge((Func<Question, CancellationToken, ValueTask<Verdict>>)null!));
        Assert.Throws<ArgumentNullException>(() => new DelegateJudge((Func<Question, CancellationToken, ValueTask<VerdictKind>>)null!));
    }

    [Fact]
    public async Task The_delegate_judge_answers_each_question_and_honors_cancellation()
    {
        var judge = new DelegateJudge((q, _) => ValueTask.FromResult(q.Similarity > 0.7 ? VerdictKind.Same : VerdictKind.Different));
        Question[] questions =
        [
            new JoinQuestion(new QuestionId(1), "a", 1, 0.8, null, []),
            new JoinQuestion(new QuestionId(2), "b", 1, 0.65, null, []),
        ];

        var verdicts = await judge.JudgeAsync(questions, default);

        Assert.Equal([Verdict.Same(new QuestionId(1)), Verdict.Different(new QuestionId(2))], verdicts);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => judge.JudgeAsync(questions, cancellation.Token));
        Assert.Throws<ArgumentNullException>(() => new DelegateJudge((Func<Question, VerdictKind>)null!));
    }

    /// <summary>A judge that answers nothing, as a broken integration might.</summary>
    private sealed class DelegateJudgeThatSkips(Action onRound) : IClusterJudge
    {
        public Task<IReadOnlyList<Verdict>> JudgeAsync(IReadOnlyList<Question> questions, CancellationToken cancellationToken)
        {
            onRound();
            return Task.FromResult<IReadOnlyList<Verdict>>([]);
        }
    }
}
