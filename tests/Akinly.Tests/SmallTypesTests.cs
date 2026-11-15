using Akinly.Storage;

namespace Akinly.Tests;

/// <summary>The small public types: ids, the text source, exceptions and records.</summary>
public sealed class SmallTypesTests
{
    [Fact]
    public void Question_ids_order_by_their_number()
    {
        var one = new QuestionId(1);
        var two = new QuestionId(2);

        Assert.True(one < two && two > one && one <= new QuestionId(1) && two >= new QuestionId(2));
        Assert.False(one > two || two < one);
        Assert.Equal(-1, one.CompareTo(two));
        Assert.Equal("2", two.ToString());
        Assert.Equal([one, two], new[] { two, one }.Order());
    }

    [Fact]
    public async Task The_text_source_keeps_replaces_and_forgets_texts()
    {
        var texts = new InMemoryItemTextStore();
        texts.Set("a", "first");
        texts.Set("a", "second");

        Assert.Equal("second", await texts.GetTextAsync("a", default));
        Assert.Equal(1, texts.Count);
        Assert.True(texts.Remove("a"));
        Assert.False(texts.Remove("a"));
        Assert.Null(await texts.GetTextAsync("a", default));
        Assert.Throws<ArgumentNullException>(() => texts.Set(null!, "x"));
        Assert.Throws<ArgumentNullException>(() => texts.Set("x", null!));
    }

    [Fact]
    public void The_exceptions_carry_their_messages_and_causes()
    {
        var cause = new InvalidOperationException("cause");

        Assert.NotEmpty(new ClusterStoreConflictException().Message);
        Assert.Equal("m", new ClusterStoreConflictException("m").Message);
        Assert.Same(cause, new ClusterStoreConflictException("m", cause).InnerException);
        Assert.Null(new ClusterStoreConflictException("m").ExpectedVersion);
        Assert.NotEmpty(new EmbeddingModelMismatchException().Message);
        Assert.Equal("m", new EmbeddingModelMismatchException("m").Message);
        Assert.Same(cause, new EmbeddingModelMismatchException("m", cause).InnerException);
    }

    [Fact]
    public void Verdict_helpers_name_their_kind()
    {
        var id = new QuestionId(7);

        Assert.Equal(
            [VerdictKind.Same, VerdictKind.Different, VerdictKind.Failed, VerdictKind.Unanswerable],
            new[] { Verdict.Same(id), Verdict.Different(id), Verdict.Failed(id), Verdict.Unanswerable(id) }.Select(v => v.Kind));
    }

    [Fact]
    public void Steps_join_in_order()
    {
        var first = new ClusteringStep([new Placement("a", 1, PlacementReason.NewCluster)], [], [], []);
        var second = new ClusteringStep([new Placement("b", 1, PlacementReason.Similarity)], [new ClusterMerge(2, 1, PlacementReason.Judge)], [new ClusterSplit(1, [3])], []);

        var both = first.Then(second);

        Assert.Equal(["a", "b"], both.Placements.Select(p => p.ItemId));
        Assert.Single(both.Merges);
        Assert.Single(both.Splits);
        Assert.Throws<ArgumentNullException>(() => first.Then(null!));
    }

    [Fact]
    public void Cosine_only_options_have_no_ask_zone()
    {
        var options = ClusteringOptions.CosineOnly("m", 0.8, 0.9);

        Assert.False(options.HasAskZone);
        Assert.Equal((0.8, 0.8, 0.9, 0.9), (options.JoinSimilarity, options.CheckSimilarity, options.MergeSimilarity, options.MergeCheckSimilarity));
        Assert.Empty(options.Validate());
    }

    [Fact]
    public void Missing_or_out_of_range_nested_options_are_named()
    {
        var options = new ClusteringOptions
        {
            EmbeddingModel = "m",
            MergeCheckSimilarity = 0.9,
            MergeSimilarity = 0.8,
            FailedAnswers = new FailedAnswerOptions { MaxAttempts = 0, RetryDelay = TimeSpan.FromSeconds(-1) },
            Split = new SplitOptions { Enabled = true, CohesionThreshold = 0, RegroupSimilarity = 2, MinPartSize = 0 },
        };

        var errors = options.Validate();

        foreach (var name in new[] { "MergeCheckSimilarity must not exceed", "MaxAttempts", "RetryDelay", "CohesionThreshold", "RegroupSimilarity", "MinPartSize", "cosine-only" })
        {
            Assert.Contains(errors, e => e.Contains(name, StringComparison.Ordinal));
        }

        Assert.Contains("FailedAnswers is required.", new ClusteringOptions { EmbeddingModel = "m", FailedAnswers = null! }.Validate());
        Assert.Contains("Split is required.", new ClusteringOptions { EmbeddingModel = "m", Split = null! }.Validate());
    }
}
