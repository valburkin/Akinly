namespace Akinly.Evaluation.Tests;

public sealed class CalibratorTests
{
    /// <summary>Groups along their own axes, with some noise in the others.</summary>
    private static (List<LabeledText> Texts, Dictionary<string, float[]> Vectors) Groups(int groups, int size, double noise, int seed = 3)
    {
        var random = new Random(seed);
        var texts = new List<LabeledText>();
        var vectors = new Dictionary<string, float[]>();
        for (var i = 0; i < groups * size; i++)
        {
            var label = i % groups;
            var id = $"t{i}";
            texts.Add(new LabeledText(id, id, $"g{label}"));
            // A direction all groups share, the group's own, and noise in every direction: groups
            // overlap the way topics in one domain do.
            vectors[id] = [1f, .. Enumerable.Range(0, groups).Select(d => (d == label ? 1f : 0f) + (float)(random.NextDouble() * noise))];
        }

        return (texts, vectors);
    }

    [Fact]
    public void Separated_groups_give_ordered_thresholds_with_high_precision()
    {
        var (texts, vectors) = Groups(6, 30, noise: 0.5);

        var calibration = Calibrator.Run("test", "test:model", texts, vectors);

        Assert.Equal((180, 6), (calibration.Items, calibration.Groups));
        Assert.InRange(calibration.CheckSimilarity, 0, calibration.JoinSimilarity);
        Assert.InRange(calibration.MergeCheckSimilarity, 0, calibration.MergeSimilarity);
        Assert.True(calibration.JoinPrecision >= 0.95);
        Assert.True(calibration.CheckRecall >= 0.95);
        Assert.True(calibration.MergePrecision >= 0.95);
        Assert.Empty(calibration.Warnings);
        Assert.NotEmpty(calibration.JoinBins);
        Assert.NotEmpty(calibration.MergeBins);
    }

    [Fact]
    public void A_band_of_mostly_wrong_candidates_is_not_hidden_by_the_many_right_ones_above_it()
    {
        // 1,000 right at 0.9, then 10 at 0.6 of which only 2 are right: everything at or above 0.6
        // is still 99% right, but a candidate at 0.6 is right 20% of the time.
        var candidates = Enumerable.Repeat((0.9, true), 1000)
            .Concat(Enumerable.Range(0, 10).Select(i => (0.6, i < 2)))
            .Concat(Enumerable.Repeat((0.75, true), 30))
            .ToList();
        var warnings = new List<string>();

        Assert.Equal(0.75, Calibrator.Certain(candidates, 0.95, warnings, "join"));
        Assert.Equal(1.0, Calibrator.Certain([(0.9, false), (0.5, true)], 0.95, warnings, "join"));
        Assert.Single(warnings);
    }

    [Fact]
    public void Thresholds_are_rounded_to_hundredths_join_up_and_check_down()
    {
        var (texts, vectors) = Groups(5, 20, noise: 0.6, seed: 9);

        var calibration = Calibrator.Run("test", "test:model", texts, vectors);

        foreach (var value in new[] { calibration.JoinSimilarity, calibration.CheckSimilarity, calibration.MergeSimilarity, calibration.MergeCheckSimilarity })
        {
            Assert.Equal(Math.Round(value, 3), value, 12);
        }

        Assert.Equal(Math.Round(calibration.CheckSimilarity, 2), calibration.CheckSimilarity, 12);

        // Rounding the join threshold up keeps the precision measured at it at the target.
        Assert.True(calibration.JoinPrecision is null or >= 0.95);
    }

    [Fact]
    public void Groups_that_cannot_be_told_apart_reach_no_threshold_and_say_so()
    {
        // Two labels on the same vectors: the nearest cluster is right about half of the time.
        var texts = Enumerable.Range(0, 40).Select(i => new LabeledText($"t{i}", "x", i % 2 == 0 ? "a" : "b")).ToList();
        var vectors = texts.ToDictionary(t => t.Id, t => new[] { 1f, int.Parse(t.Id[1..], System.Globalization.CultureInfo.InvariantCulture) % 4 < 2 ? 0.1f : 0.2f });

        var calibration = Calibrator.Run("test", "test:model", texts, vectors);

        Assert.Equal(1.0, calibration.JoinSimilarity);
        Assert.Contains(calibration.Warnings, w => w.Contains("join", StringComparison.Ordinal));
        Assert.Contains("Note:", Calibrator.Markdown(calibration), StringComparison.Ordinal);
    }

    [Fact]
    public void The_markdown_shows_both_tables()
    {
        var (texts, vectors) = Groups(3, 10, noise: 0.3);

        var markdown = Calibrator.Markdown(Calibrator.Run("test", "test:model", texts, vectors));

        Assert.Contains("`JoinSimilarity`", markdown, StringComparison.Ordinal);
        Assert.Contains("nearest cluster so far", markdown, StringComparison.Ordinal);
        Assert.Contains("Pairs of parts", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void The_preference_sets_order_the_join_and_merge_thresholds_and_keep_the_ask_zones()
    {
        var (texts, vectors) = Groups(6, 30, noise: 0.7);

        var c = Calibrator.Run("test", "test:model", texts, vectors);

        Assert.True(c.Precision.JoinSimilarity >= c.JoinSimilarity && c.JoinSimilarity >= c.Recall.JoinSimilarity);
        Assert.True(c.Precision.MergeSimilarity >= c.MergeSimilarity && c.MergeSimilarity >= c.Recall.MergeSimilarity);
        Assert.Equal(Math.Min(c.Recall.JoinSimilarity, c.CheckSimilarity), c.Recall.CheckSimilarity);
        Assert.Equal(Math.Min(c.Precision.MergeSimilarity, c.MergeCheckSimilarity), c.Precision.MergeCheckSimilarity);
    }

    [Fact]
    public void Datasets_without_a_split_are_known()
    {
        Assert.False(Dataset.HasSplit("news-t"));
        Assert.False(Dataset.HasSplit("tweets-t"));
        Assert.True(Dataset.HasSplit("massive"));
        Assert.True(Dataset.IsPublic("intents"));
        Assert.False(Dataset.HasSingleSource("intents"));
        foreach (var name in new[] { "banking77", "clinc150", "massive", "news-t", "tweets-t" })
        {
            Assert.True(Dataset.HasSingleSource(name));
            Assert.NotNull(Dataset.Source(name, "train"));
            Assert.NotNull(Dataset.Source(name, "test"));
        }
    }
}
