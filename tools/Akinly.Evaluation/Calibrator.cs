using System.Globalization;
using System.Text;

namespace Akinly.Evaluation;

/// <summary>
/// Suggests thresholds for an embedding model from labeled data, by replaying the data the way
/// the clusterer sees it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Joins.</b> The items arrive one at a time, in a random order, into clusters built from
/// the labels (what a perfect clusterer would build). Each item is compared with every cluster
/// so far, as the clusterer compares it with its candidates, before it joins its own. So the
/// clusters it meets have every size from one member up, and its own group may not have a
/// cluster yet, as early in a stream. The join threshold is the lowest similarity at which the
/// nearest cluster is the item's own at least <c>precision</c> of the time (fitted as rising
/// with similarity, see <see cref="Certain"/>). A judge is asked only about the item's nearest
/// clusters, <c>maxChecks</c> of them; the check threshold keeps all but <c>loss</c> of the
/// items whose own cluster is among those within a judge's reach.
/// </para>
/// <para>
/// <b>Merges.</b> Each group is cut in two, a fragment of one to five items and the rest, as a
/// group the clusterer left in two clusters, and every pair of parts is compared, since maintenance merges every pair
/// at or above the merge threshold in time. The merge threshold is the lowest similarity at which
/// a pair of parts is of one group at least <c>precision</c> of the time; the merge check
/// threshold keeps all but <c>loss</c> of the true pairs whose parts are among each other's
/// <c>candidates</c> nearest.
/// </para>
/// <para>
/// Join and merge thresholds are rounded up to two decimals (three if all candidates are closer
/// than two can tell apart), check thresholds down, and the rates reported are those at the
/// rounded values.
/// </para>
/// <para>
/// <b>Preferences.</b> The suggested values are balanced: <c>precision</c> 0.95. The report adds
/// a set that prefers precision (joins and merges right 98% of the time) and one that prefers
/// recall (90%), with the same check thresholds, to pair with the judge's matching
/// <c>ClusteringPreference</c>.
/// </para>
/// </remarks>
public static class Calibrator
{
    /// <summary>The largest fragment a merge is calibrated on.</summary>
    private const int MaxFragment = 5;

    public static Calibration Run(
        string dataset, string embedder, IReadOnlyList<LabeledText> texts, IReadOnlyDictionary<string, float[]> vectors,
        double precision = 0.95, double loss = 0.01, int seed = 1, int maxChecks = 5, int candidates = 10)
    {
        var random = new Random(seed);
        var unit = texts.ToDictionary(t => t.Id, t => Vectors.Normalize(vectors[t.Id]), StringComparer.Ordinal);
        var warnings = new List<string>();

        // Joins: replay the items into clusters made from the labels.
        var nearest = new List<(double Similarity, bool Right)>();
        var own = new List<(double Similarity, int Rank)>();
        var sums = new Dictionary<string, double[]>(StringComparer.Ordinal);
        var centroids = new Dictionary<string, float[]>(StringComparer.Ordinal);
        foreach (var text in texts.OrderBy(_ => random.Next()))
        {
            var vector = unit[text.Id];
            if (centroids.Count > 0)
            {
                var ranked = centroids.Select(c => (Label: c.Key, Similarity: Vectors.Dot(vector, c.Value))).OrderByDescending(c => c.Similarity).ToList();
                nearest.Add((ranked[0].Similarity, ranked[0].Label == text.Label));
                var rank = ranked.FindIndex(c => c.Label == text.Label);
                if (rank >= 0)
                {
                    own.Add((ranked[rank].Similarity, rank + 1));
                }
            }

            var sum = sums.TryGetValue(text.Label, out var existing) ? existing : sums[text.Label] = new double[vector.Length];
            for (var d = 0; d < vector.Length; d++)
            {
                sum[d] += vector[d];
            }

            centroids[text.Label] = Direction(sum);
        }

        // Merges: each group cut in two, a small fragment and the rest, every pair of parts
        // compared. A fragment is what the clusterer leaves apart: a few items that came early or
        // were refused, rarely half a group.
        var parts = new List<(string Label, float[] Centroid)>();
        foreach (var group in texts.GroupBy(t => t.Label).Where(g => g.Count() >= 2).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var members = group.OrderBy(_ => random.Next()).Select(t => unit[t.Id]).ToList();
            var cut = random.Next(1, Math.Min(MaxFragment, members.Count - 1) + 1);
            parts.Add((group.Key, Vectors.Mean([.. members.Take(cut)])));
            parts.Add((group.Key, Vectors.Mean([.. members.Skip(cut)])));
        }

        var pairs = new List<(double Similarity, bool Right)>();
        var twins = new List<(double Similarity, int Rank)>();
        for (var i = 0; i < parts.Count; i++)
        {
            var ranked = Enumerable.Range(0, parts.Count).Where(j => j != i)
                .Select(j => (Index: j, Similarity: Vectors.Dot(parts[i].Centroid, parts[j].Centroid))).OrderByDescending(p => p.Similarity).ToList();
            pairs.AddRange(ranked.Where(p => p.Index > i).Select(p => (p.Similarity, parts[p.Index].Label == parts[i].Label)));
            if (i % 2 == 0)
            {
                var rank = ranked.FindIndex(p => parts[p.Index].Label == parts[i].Label);
                twins.Add((ranked[rank].Similarity, rank + 1));
            }
        }

        var join = RoundUp(Certain(nearest, precision, warnings, "join"), nearest);
        var reachable = own.Where(o => o.Rank <= maxChecks).Select(o => o.Similarity).ToList();
        var check = Math.Min(join, RoundDown(Percentile(reachable, loss)));
        var merge = RoundUp(Certain(pairs, precision, warnings, "merge"), pairs);
        var mergeReachable = twins.Where(t => t.Rank <= candidates).Select(t => t.Similarity).ToList();
        var mergeCheck = Math.Min(merge, RoundDown(Percentile(mergeReachable, loss)));
        var groups = texts.Select(t => t.Label).Distinct(StringComparer.Ordinal).Count();

        // The other preferences: the same check thresholds, joins and merges certain to another degree.
        // Their warnings are not reported; the balanced set's are.
        ThresholdSet Prefer(double target)
        {
            var quiet = new List<string>();
            var preferJoin = RoundUp(Certain(nearest, target, quiet, "join"), nearest);
            var preferMerge = RoundUp(Certain(pairs, target, quiet, "merge"), pairs);
            return new ThresholdSet(preferJoin, Math.Min(preferJoin, check), preferMerge, Math.Min(preferMerge, mergeCheck));
        }

        return new Calibration(
            dataset, embedder, texts.Count, groups,
            join, check, merge, mergeCheck,
            PrecisionAt(nearest, join),
            Share(nearest.Count(p => p.Right && p.Similarity >= join), own.Count),
            Share(own.Count(o => o.Rank <= maxChecks && o.Similarity >= check), own.Count),
            PrecisionAt(pairs, merge),
            Share(twins.Count(t => t.Rank <= candidates && t.Similarity >= mergeCheck), twins.Count),
            Bins(nearest), Bins(pairs), warnings,
            Prefer(Math.Max(precision, 0.98)), Prefer(Math.Min(precision, 0.90)));
    }

    /// <summary>
    /// The lowest similarity at which a candidate is right at least <paramref name="precision"/>
    /// of the time, or 1 if there is none. How often a candidate is right is fitted as a
    /// non-decreasing function of its similarity (isotonic regression, by pooling adjacent
    /// violators), so a band of mostly wrong candidates is never hidden by the many easy ones
    /// above it.
    /// </summary>
    internal static double Certain(List<(double Similarity, bool Right)> candidates, double precision, List<string> warnings, string what)
    {
        // Blocks of (lowest similarity, right, count), ascending; equal similarities share one.
        var blocks = new List<(double From, double Right, double Count)>();
        foreach (var tie in candidates.GroupBy(c => c.Similarity).OrderBy(g => g.Key))
        {
            blocks.Add((tie.Key, tie.Count(c => c.Right), tie.Count()));
            while (blocks.Count > 1 && blocks[^2].Right / blocks[^2].Count > blocks[^1].Right / blocks[^1].Count)
            {
                var (low, high) = (blocks[^2], blocks[^1]);
                blocks.RemoveAt(blocks.Count - 1);
                blocks[^1] = (low.From, low.Right + high.Right, low.Count + high.Count);
            }
        }

        foreach (var block in blocks)
        {
            if (block.Right / block.Count >= precision)
            {
                return block.From;
            }
        }

        warnings.Add($"No similarity reached {precision:P0} right for {what}s: the suggested 1.00 means never {what} without a judge.");
        return 1;
    }

    private static double? PrecisionAt(List<(double Similarity, bool Right)> candidates, double threshold)
    {
        var above = candidates.Where(p => p.Similarity >= threshold).ToList();
        return above.Count == 0 ? null : (double)above.Count(p => p.Right) / above.Count;
    }

    /// <summary>The value with <paramref name="share"/> of the values below it.</summary>
    private static double Percentile(List<double> values, double share) =>
        values.Count == 0 ? 1 : values.Order().ElementAt((int)Math.Floor(share * (values.Count - 1)));

    private static double Share(int count, int total) => total == 0 ? 0 : (double)count / total;

    /// <summary>
    /// Rounded up to two decimals, or to three when two would pass every candidate (all of them
    /// above 0.99, say), so the threshold still means something.
    /// </summary>
    private static double RoundUp(double value, List<(double Similarity, bool Right)> candidates)
    {
        static double Up(double value, double scale) => Math.Min(1, Math.Ceiling((value * scale) - 1e-9) / scale);
        return value >= 1 || candidates.Any(c => c.Similarity >= Up(value, 100)) ? Up(value, 100) : Up(value, 1000);
    }

    private static double RoundDown(double value) => Math.Floor((value * 100) + 1e-9) / 100;

    private static float[] Direction(double[] sum)
    {
        var length = Math.Sqrt(sum.Sum(v => v * v));
        return [.. sum.Select(v => (float)(v / length))];
    }

    private static List<Bin> Bins(List<(double Similarity, bool Right)> candidates)
    {
        var bins = new List<Bin>();
        if (candidates.Count == 0)
        {
            return bins;
        }

        // Duplicate texts can come out a rounding above 1: they count in the last range.
        for (var step = (int)Math.Floor(Math.Min(candidates.Min(p => p.Similarity), 0.999) * 20); step < 20; step++)
        {
            var (from, to) = (step / 20.0, (step + 1) / 20.0);
            var inBin = candidates.Where(p => p.Similarity >= from && (p.Similarity < to || step == 19)).ToList();
            if (inBin.Count > 0)
            {
                bins.Add(new Bin(from, to, inBin.Count, inBin.Count(p => p.Right)));
            }
        }

        return bins;
    }

    private static string Percent(double? share) =>
        share is { } value ? string.Create(CultureInfo.InvariantCulture, $"{value * 100:0}%") : "–";

    public static string Markdown(Calibration c)
    {
        var text = new StringBuilder();
        var culture = CultureInfo.InvariantCulture;
        text.AppendLine(culture, $"## {c.Embedder} on {c.Dataset}");
        text.AppendLine();
        text.AppendLine(culture, $"{c.Items:N0} items in {c.Groups} groups, replayed in a random order into clusters made from their labels. Suggested thresholds:");
        text.AppendLine();
        text.AppendLine("| Option | Value | Why |");
        text.AppendLine("|---|---|---|");
        text.AppendLine(culture, $"| `JoinSimilarity` | {c.JoinSimilarity:0.00#} | At or above it, the nearest cluster was the item's own {Percent(c.JoinPrecision)} of the time; {Percent(c.JoinRecall)} of the items whose group had a cluster joined it without a question |");
        text.AppendLine(culture, $"| `CheckSimilarity` | {c.CheckSimilarity:0.00#} | {Percent(c.CheckRecall)} of the items whose group had a cluster would have it put to a judge: among their nearest that a judge is asked about, and at least this close |");
        text.AppendLine(culture, $"| `MergeSimilarity` | {c.MergeSimilarity:0.00#} | At or above it, a pair of parts was of one group {Percent(c.MergePrecision)} of the time |");
        text.AppendLine(culture, $"| `MergeCheckSimilarity` | {c.MergeCheckSimilarity:0.00#} | {Percent(c.MergeCheckRecall)} of the groups cut in two had their parts among each other's ten nearest and at least this close |");
        text.AppendLine();
        text.AppendLine("By preference (pair it with the judge's `ClusteringPreference`; the check thresholds stay the same):");
        text.AppendLine();
        text.AppendLine("| Preference | `JoinSimilarity` | `CheckSimilarity` | `MergeSimilarity` | `MergeCheckSimilarity` |");
        text.AppendLine("|---|---|---|---|---|");
        text.AppendLine(culture, $"| Precision | {c.Precision.JoinSimilarity:0.00#} | {c.Precision.CheckSimilarity:0.00#} | {c.Precision.MergeSimilarity:0.00#} | {c.Precision.MergeCheckSimilarity:0.00#} |");
        text.AppendLine(culture, $"| Balanced (suggested) | {c.JoinSimilarity:0.00#} | {c.CheckSimilarity:0.00#} | {c.MergeSimilarity:0.00#} | {c.MergeCheckSimilarity:0.00#} |");
        text.AppendLine(culture, $"| Recall | {c.Recall.JoinSimilarity:0.00#} | {c.Recall.CheckSimilarity:0.00#} | {c.Recall.MergeSimilarity:0.00#} | {c.Recall.MergeCheckSimilarity:0.00#} |");
        foreach (var warning in c.Warnings)
        {
            text.AppendLine();
            text.AppendLine(culture, $"Note: {warning}");
        }

        Table(text, "An item and the nearest cluster so far, by similarity:", "Same group", c.JoinBins);
        Table(text, "Pairs of parts of groups cut in two, by similarity:", "Same group", c.MergeBins);
        return text.ToString();
    }

    private static void Table(StringBuilder text, string title, string right, IReadOnlyList<Bin> bins)
    {
        var culture = CultureInfo.InvariantCulture;
        text.AppendLine();
        text.AppendLine(title);
        text.AppendLine();
        text.AppendLine(culture, $"| Similarity | Candidates | {right} |");
        text.AppendLine("|---|---|---|");
        foreach (var bin in bins)
        {
            text.AppendLine(culture, $"| {bin.From:0.00}–{bin.To:0.00} | {bin.Candidates:N0} | {Percent((double)bin.Right / bin.Candidates)} |");
        }
    }
}
