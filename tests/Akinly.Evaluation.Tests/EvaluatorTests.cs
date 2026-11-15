namespace Akinly.Evaluation.Tests;

public sealed class EvaluatorTests
{
    private static readonly List<LabeledText> Texts =
    [
        new("a1", "a", "east"), new("a2", "a", "east"), new("a3", "a", "east"),
        new("b1", "b", "north"), new("b2", "b", "north"), new("b3", "b", "north"),
    ];

    private static readonly Dictionary<string, float[]> Vectors = new()
    {
        ["a1"] = [1f, 0f],
        ["a2"] = [0.99f, 0.14f],
        ["a3"] = [0.8f, 0.6f],
        ["b1"] = [0f, 1f],
        ["b2"] = [0.14f, 0.99f],
        ["b3"] = [0.6f, 0.8f],
    };

    [Fact]
    public async Task Cosine_only_puts_close_items_together_and_scores_them()
    {
        var (scores, _) = await Evaluator.CosineAsync(Texts, Vectors, ClusteringOptions.CosineOnly("test", 0.97), batchSize: 2, default);

        Assert.Equal(4, scores.Clusters);   // a3 and b3 each sit alone at this threshold
        Assert.Equal(1.0, scores.PairPrecision);
    }

    [Fact]
    public async Task A_judge_settles_the_close_calls_and_its_failures_are_counted()
    {
        var options = new ClusteringOptions { EmbeddingModel = "test", JoinSimilarity = 0.95, CheckSimilarity = 0.5, MergeSimilarity = 0.99, MergeCheckSimilarity = 0.9 };
        options.FailedAnswers.RetryDelay = TimeSpan.Zero;
        var judge = new CountingJudge(new DelegateJudge(q => q is JoinQuestion join
            ? join.ItemId == "b3" ? VerdictKind.Failed : VerdictKind.Same
            : VerdictKind.Different));

        var progress = new List<int>();

        var (scores, _) = await Evaluator.JudgedAsync(Texts, Vectors, options, judge, batchSize: 3, (done, _) => progress.Add(done), default);

        // a3 joins on the judge's word; b3's question fails every time, so it starts its own cluster.
        Assert.Equal((3, 1), (scores.Clusters, scores.Singletons));
        Assert.Equal(1.0, scores.PairPrecision);
        Assert.True(judge.Failed >= 1 && judge.Failed < judge.Verdicts);
        Assert.Equal([3, 6], progress);
    }

    [Fact]
    public void A_shuffle_is_repeatable_for_a_seed()
    {
        Assert.Equal(Evaluator.Shuffle(Texts, 4), Evaluator.Shuffle(Texts, 4));
        Assert.Equal(Texts.OrderBy(t => t.Id), Evaluator.Shuffle(Texts, 4).OrderBy(t => t.Id));
    }
}
