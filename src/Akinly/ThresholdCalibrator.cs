using Akinly.Storage;

namespace Akinly;

/// <summary>
/// Finds where the ask zones should start for your data, from your judge's own answers. Absolute
/// similarities do not carry over between kinds of text, even with one embedding model: texts
/// written on one template, say, are all more similar to each other than the defaults expect,
/// and a judge asked about them at low similarity says yes rarely and wrongly more often.
/// </summary>
/// <remarks>
/// <para>
/// The calibrator clusters a sample of your items in a throwaway in-memory store, with the ask
/// zones opened down to <see cref="CalibrationOptions.LowestCheckSimilarity"/> and
/// <see cref="CalibrationOptions.LowestMergeCheckSimilarity"/>, and counts how often the judge
/// says yes in each band of similarity: for joins, only each item's first question, since later
/// ones go to clusters the item is less likely to fit. The yes-rates are fitted as rising with
/// similarity, and each floor is set at the lowest band whose fitted rate reaches
/// <see cref="CalibrationOptions.JoinYesRate"/> or <see cref="CalibrationOptions.MergeYesRate"/>.
/// </para>
/// <para>
/// It is an explicit step, run before clustering for real (and again when the data changes):
/// the thresholds of a clusterer never shift while it runs. It costs up to one join question
/// per item with the default options, and some merge questions; a sample of 1,000 to 2,000 items is usually enough. Its
/// result is deterministic for the same items, options and answers.
/// </para>
/// </remarks>
public static class ThresholdCalibrator
{
    private const int MaxSettleRounds = 50;
    private const int FewJoinQuestions = 200;
    private const int FewMergeQuestions = 100;

    /// <summary>Clusters a sample with wide ask zones and suggests where they should start.</summary>
    /// <param name="sample">Items like those you will cluster, in the order they would arrive; ids unique.</param>
    /// <param name="judge">The judge you will cluster with.</param>
    /// <param name="options">
    /// The options you will cluster with. Their thresholds and <see cref="ClusteringOptions.JoinChoices"/>
    /// shape the trial run, except the floors, which the calibration explores.
    /// </param>
    /// <param name="calibration">How to explore and where to set the floors; the defaults when null.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The yes-rates by band and the suggested floors; apply them with <see cref="ThresholdCalibration.ApplyTo"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sample"/>, <paramref name="judge"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">The options or the calibration options are invalid, or the sample is empty or repeats an id.</exception>
    public static async Task<ThresholdCalibration> CalibrateAsync(
        IEnumerable<ClusterItem> sample, IClusterJudge judge, ClusteringOptions options, CalibrationOptions? calibration = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentNullException.ThrowIfNull(judge);
        ArgumentNullException.ThrowIfNull(options);
        calibration ??= new CalibrationOptions();
        var errors = calibration.Validate();
        if (errors.Count > 0)
        {
            throw new ArgumentException("Invalid calibration options: " + string.Join(" ", errors), nameof(calibration));
        }

        // Splitting is for cosine-only setups; the trial run asks a judge, so it never splits.
        var trial = options.Clone();
        trial.CheckSimilarity = Math.Min(calibration.LowestCheckSimilarity, trial.JoinSimilarity);
        trial.MergeCheckSimilarity = Math.Min(calibration.LowestMergeCheckSimilarity, trial.MergeSimilarity);
        trial.Split = new SplitOptions();
        var items = sample.ToList();
        if (items.Count == 0)
        {
            throw new ArgumentException("The sample is empty.", nameof(sample));
        }

        if (items.Select(i => i?.Id).Distinct(StringComparer.Ordinal).Count() != items.Count)
        {
            throw new ArgumentException("The sample repeats an item id.", nameof(sample));
        }

        var recorder = new Recorder(judge);
        var clusterer = new Clusterer(new InMemoryClusterStore(), trial);
        foreach (var batch in items.Chunk(calibration.BatchSize))
        {
            await clusterer.SettleAsync(batch, recorder, cancellationToken).ConfigureAwait(false);
            await clusterer.MaintainAsync(cancellationToken).ConfigureAwait(false);
            await clusterer.SettleAsync(recorder, cancellationToken).ConfigureAwait(false);
        }

        var settled = false;
        for (var round = 0; round < MaxSettleRounds && !settled; round++)
        {
            var step = await clusterer.MaintainAsync(cancellationToken).ConfigureAwait(false);
            step = step.Then(await clusterer.SettleAsync(recorder, cancellationToken).ConfigureAwait(false));
            settled = step.Questions.Count == 0 && step.Merges.Count == 0;
        }

        var warnings = new List<string>();
        if (!settled)
        {
            warnings.Add($"Merges were still going on after {MaxSettleRounds} rounds of maintenance: the calibration stopped there.");
        }

        var joinBands = Bands(recorder.Joins, calibration.BandWidth);
        var mergeBands = Bands(recorder.Merges, calibration.BandWidth);
        var check = Floor(joinBands, calibration.JoinYesRate, trial.CheckSimilarity, trial.JoinSimilarity, "join", warnings) ?? options.CheckSimilarity;
        var mergeCheck = Floor(mergeBands, calibration.MergeYesRate, trial.MergeCheckSimilarity, trial.MergeSimilarity, "merge", warnings) ?? options.MergeCheckSimilarity;
        if (recorder.Joins.Count < FewJoinQuestions)
        {
            warnings.Add($"Only {recorder.Joins.Count} join questions were answered: the check floor is uncertain. Calibrate on a larger sample.");
        }

        if (recorder.Merges.Count < FewMergeQuestions)
        {
            warnings.Add($"Only {recorder.Merges.Count} merge questions were answered: the merge-check floor is uncertain. Calibrate on a larger sample.");
        }

        return new ThresholdCalibration(joinBands, mergeBands, check, mergeCheck, warnings);
    }

    /// <summary>The answers grouped into bands of similarity, lowest first, empty bands left out.</summary>
    internal static List<SimilarityBand> Bands(IReadOnlyList<(double Similarity, bool Yes)> answers, double width) =>
        [.. answers
            .GroupBy(a => (int)Math.Floor((a.Similarity + 1e-9) / width))
            .OrderBy(g => g.Key)
            .Select(g => new SimilarityBand(Math.Round(g.Key * width, 4), Math.Round((g.Key + 1) * width, 4), g.Count(), g.Count(a => a.Yes)))];

    /// <summary>
    /// The lowest band start from which the yes-rate, fitted as rising with similarity, reaches the
    /// target; the explored floor when even the lowest band reaches it, the threshold above the
    /// zone (no asking) when none does, and null (leave the floor as it was) with no answers.
    /// </summary>
    internal static double? Floor(IReadOnlyList<SimilarityBand> bands, double target, double explored, double ceiling, string kind, List<string> warnings)
    {
        if (bands.Count == 0)
        {
            warnings.Add($"No {kind} question was answered: the {kind} floor is left as the options set it.");
            return null;
        }

        var fitted = Isotonic(bands);
        var first = fitted.FindIndex(rate => rate >= target);
        if (first < 0)
        {
            warnings.Add($"The judge's {kind} yes-rate never reached {target:0.##}: it is not worth asking about {kind}s here, so the floor is set to the threshold above the zone.");
            return ceiling;
        }

        if (first == 0)
        {
            warnings.Add($"The judge's {kind} yes-rate already reached {target:0.##} at the lowest similarity explored: a lower floor might pay, so explore from lower.");
            return explored;
        }

        return Math.Max(explored, bands[first].From);
    }

    /// <summary>The bands' yes-rates fitted as non-decreasing (pool adjacent violators), weighted by their questions.</summary>
    internal static List<double> Isotonic(IReadOnlyList<SimilarityBand> bands)
    {
        var blocks = new List<(double Yes, double Questions, int Count)>();
        foreach (var band in bands)
        {
            blocks.Add((band.Yes, band.Questions, 1));
            while (blocks.Count > 1 && blocks[^2].Yes / blocks[^2].Questions > blocks[^1].Yes / blocks[^1].Questions)
            {
                var (last, previous) = (blocks[^1], blocks[^2]);
                blocks.RemoveAt(blocks.Count - 1);
                blocks[^1] = (previous.Yes + last.Yes, previous.Questions + last.Questions, previous.Count + last.Count);
            }
        }

        return [.. blocks.SelectMany(b => Enumerable.Repeat(b.Yes / b.Questions, b.Count))];
    }

    /// <summary>A judge that passes questions on and records each first join question's and each merge question's answer.</summary>
    private sealed class Recorder(IClusterJudge inner) : IClusterJudge
    {
        private readonly Lock _lock = new();
        private readonly Dictionary<string, QuestionId> _firstJoin = new(StringComparer.Ordinal);
        private readonly HashSet<QuestionId> _recorded = [];

        public List<(double Similarity, bool Yes)> Joins { get; } = [];

        public List<(double Similarity, bool Yes)> Merges { get; } = [];

        public async Task<IReadOnlyList<Verdict>> JudgeAsync(IReadOnlyList<Question> questions, CancellationToken cancellationToken)
        {
            var verdicts = await inner.JudgeAsync(questions, cancellationToken).ConfigureAwait(false);
            lock (_lock)
            {
                var byId = new Dictionary<QuestionId, Verdict>();
                foreach (var verdict in verdicts)
                {
                    byId.TryAdd(verdict.QuestionId, verdict);
                }

                foreach (var question in questions)
                {
                    if (question is JoinQuestion asked && !_firstJoin.ContainsKey(asked.ItemId))
                    {
                        _firstJoin[asked.ItemId] = asked.Id;
                    }

                    if (!byId.TryGetValue(question.Id, out var verdict) || verdict.Kind is not (VerdictKind.Same or VerdictKind.Different)
                        || _recorded.Contains(question.Id))
                    {
                        continue;
                    }

                    switch (question)
                    {
                        case JoinQuestion join when _firstJoin[join.ItemId] == join.Id:
                            Joins.Add((join.Similarity, verdict.Kind == VerdictKind.Same));
                            _recorded.Add(question.Id);
                            break;
                        case MergeQuestion merge:
                            Merges.Add((merge.Similarity, verdict.Kind == VerdictKind.Same));
                            _recorded.Add(question.Id);
                            break;
                        default:
                            break;
                    }
                }
            }

            return verdicts;
        }
    }
}
