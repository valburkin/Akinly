using static Akinly.Tests.TestVectors;

namespace Akinly.Tests;

public sealed class ThresholdCalibratorTests
{
    /// <summary>Items in random directions of a small space, seeded: a wide range of similarities to each other.</summary>
    private static List<ClusterItem> Spread(int count)
    {
        var random = new Random(7);
        return [.. Enumerable.Range(0, count).Select(i => Item($"i{i}", [.. Enumerable.Range(0, 5).Select(_ => (float)((random.NextDouble() * 2) - 1))]))];
    }

    private static ClusteringOptions Options() => Judged(o =>
    {
        o.JoinSimilarity = 0.97;
        o.MergeSimilarity = 0.99;
        o.MaxChecksPerItem = 2;
    });

    // Yes to a join from 0.8 and to a merge from 0.9, no below.
    private static readonly DelegateJudge Step = new(q => q switch
    {
        JoinQuestion j => j.Similarity >= 0.8 ? VerdictKind.Same : VerdictKind.Different,
        MergeQuestion m => m.Similarity >= 0.9 ? VerdictKind.Same : VerdictKind.Different,
        _ => VerdictKind.Different,
    });

    [Fact]
    public async Task The_floors_land_where_the_judge_starts_saying_yes()
    {
        var calibration = await ThresholdCalibrator.CalibrateAsync(Spread(300), Step, Options());

        Assert.InRange(calibration.CheckSimilarity, 0.8, 0.84);
        Assert.InRange(calibration.MergeCheckSimilarity, 0.9, 0.94);
        Assert.All(calibration.JoinBands, b => Assert.Equal(b.From >= 0.8 ? 1.0 : 0.0, b.YesRate));
        Assert.Contains(calibration.JoinBands, b => b.From < 0.5);
        Assert.All(calibration.JoinBands.Zip(calibration.JoinBands.Skip(1)), p => Assert.True(p.First.To <= p.Second.From + 1e-9));

        var applied = calibration.ApplyTo(Options());
        Assert.Equal(calibration.CheckSimilarity, applied.CheckSimilarity);
        Assert.Equal(calibration.MergeCheckSimilarity, applied.MergeCheckSimilarity);
        Assert.Equal(0.97, applied.JoinSimilarity);
    }

    [Fact]
    public async Task Only_each_items_first_join_question_counts()
    {
        var items = Spread(200);
        var firstQuestions = new HashSet<string>();
        var judge = new DelegateJudge(q =>
        {
            if (q is JoinQuestion j)
            {
                firstQuestions.Add(j.ItemId);
            }

            return VerdictKind.Different;
        });

        var calibration = await ThresholdCalibrator.CalibrateAsync(items, judge, Options());

        Assert.Equal(firstQuestions.Count, calibration.JoinBands.Sum(b => b.Questions));
        Assert.Equal(0, calibration.JoinBands.Sum(b => b.Yes));
    }

    [Fact]
    public async Task A_judge_that_never_says_yes_closes_the_zones()
    {
        var calibration = await ThresholdCalibrator.CalibrateAsync(Spread(200), new DelegateJudge(_ => VerdictKind.Different), Options());

        Assert.Equal(0.97, calibration.CheckSimilarity);
        Assert.Equal(0.99, calibration.MergeCheckSimilarity);
        Assert.Contains(calibration.Warnings, w => w.Contains("never reached", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_judge_that_always_says_yes_keeps_the_explored_floor_and_warns()
    {
        var calibration = await ThresholdCalibrator.CalibrateAsync(
            Spread(100), new DelegateJudge(_ => VerdictKind.Same), Options(), new CalibrationOptions { LowestCheckSimilarity = 0.2 });

        Assert.Equal(0.2, calibration.CheckSimilarity);
        Assert.Contains(calibration.Warnings, w => w.Contains("explore from lower", StringComparison.Ordinal));
        Assert.Contains(calibration.Warnings, w => w.Contains("join questions were answered", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_dip_in_the_yes_rate_is_pooled_rather_than_taken_as_the_floor()
    {
        // Yes in a low band, no above it, yes again from 0.8: fitted as rising, the low band's
        // yeses are pooled with the noes above them, and the floor stays near 0.8.
        var judge = new DelegateJudge(q => q switch
        {
            JoinQuestion j => j.Similarity is >= 0.5 and < 0.52 || j.Similarity >= 0.8 ? VerdictKind.Same : VerdictKind.Different,
            _ => VerdictKind.Different,
        });

        var calibration = await ThresholdCalibrator.CalibrateAsync(Spread(300), judge, Options(), new CalibrationOptions { JoinYesRate = 0.5 });

        Assert.Contains(calibration.JoinBands, b => b.From == 0.5 && b.YesRate > 0.5);
        Assert.InRange(calibration.CheckSimilarity, 0.8, 0.84);
    }

    [Fact]
    public async Task Failed_answers_are_not_counted()
    {
        var calibration = await ThresholdCalibrator.CalibrateAsync(Spread(50), new DelegateJudge(_ => VerdictKind.Failed), Options());

        Assert.Empty(calibration.JoinBands);
        Assert.Empty(calibration.MergeBands);
        Assert.Equal((Options().CheckSimilarity, Options().MergeCheckSimilarity), (calibration.CheckSimilarity, calibration.MergeCheckSimilarity));
        Assert.Contains(calibration.Warnings, w => w.Contains("No join question", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_same_sample_and_answers_give_the_same_calibration()
    {
        var first = await ThresholdCalibrator.CalibrateAsync(Spread(150), Step, Options());
        var second = await ThresholdCalibrator.CalibrateAsync(Spread(150), Step, Options());

        Assert.Equal(first.JoinBands, second.JoinBands);
        Assert.Equal(first.MergeBands, second.MergeBands);
        Assert.Equal((first.CheckSimilarity, first.MergeCheckSimilarity), (second.CheckSimilarity, second.MergeCheckSimilarity));
    }

    [Fact]
    public async Task Bad_arguments_are_refused()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => ThresholdCalibrator.CalibrateAsync(null!, Step, Options()));
        await Assert.ThrowsAsync<ArgumentNullException>(() => ThresholdCalibrator.CalibrateAsync(Spread(5), null!, Options()));
        await Assert.ThrowsAsync<ArgumentNullException>(() => ThresholdCalibrator.CalibrateAsync(Spread(5), Step, null!));
        await Assert.ThrowsAsync<ArgumentException>(() => ThresholdCalibrator.CalibrateAsync([], Step, Options()));
        await Assert.ThrowsAsync<ArgumentException>(() => ThresholdCalibrator.CalibrateAsync([.. Spread(3), .. Spread(1)], Step, Options()));
        var bad = await Assert.ThrowsAsync<ArgumentException>(() => ThresholdCalibrator.CalibrateAsync(
            Spread(5), Step, Options(), new CalibrationOptions { JoinYesRate = 0, BandWidth = 1, BatchSize = 0, LowestCheckSimilarity = double.NaN }));
        Assert.Contains("JoinYesRate", bad.Message, StringComparison.Ordinal);
        Assert.Contains("BandWidth", bad.Message, StringComparison.Ordinal);
        Assert.Contains("BatchSize", bad.Message, StringComparison.Ordinal);
        Assert.Contains("LowestCheckSimilarity", bad.Message, StringComparison.Ordinal);
        var calibration = await ThresholdCalibrator.CalibrateAsync(Spread(1), Step, Options());
        Assert.Throws<ArgumentNullException>(() => calibration.ApplyTo(null!));
    }

    [Fact]
    public void A_band_without_questions_has_no_yes_rate() =>
        Assert.Equal(0, new SimilarityBand(0.5, 0.52, 0, 0).YesRate);
}
