using System.Globalization;
using Akinly.AI;
using Akinly.Storage;
using Microsoft.Extensions.AI;

namespace Akinly.Evaluation;

/// <summary>
/// What a reader of the clusters would see, without relying on labels: how many items sit in
/// clusters of three or more (coverage), how many near-duplicate clusters stayed apart and why,
/// how often the judge said yes at each similarity, and, with a referee model, whether each
/// cluster's title honestly describes a sample of its members, by how the member got there.
/// </summary>
/// <remarks>
/// The referee measures precision as a reader sees it: a wrong join is visible on the page, a
/// missed one is not. It sees one title and one statement at a time, never how the statement
/// was placed.
/// </remarks>
public sealed class RunReport
{
    private const int CoverageSize = 3;

    private readonly Dictionary<string, (long Cluster, PlacementReason Reason)> _placed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _joinQuestions = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();
    private readonly Dictionary<string, QuestionId> _firstJoin = new(StringComparer.Ordinal);
    private readonly HashSet<QuestionId> _answered = [];
    private readonly List<(string Kind, double Similarity, bool Yes)> _answers = [];

    /// <summary>Records what one step placed and asked.</summary>
    public void Observe(ClusteringStep step)
    {
        foreach (var placement in step.Placements)
        {
            _placed[placement.ItemId] = (placement.ClusterId, placement.Reason);
        }

        foreach (var question in step.Questions.OfType<JoinQuestion>())
        {
            _joinQuestions[question.ItemId] = _joinQuestions.GetValueOrDefault(question.ItemId) + 1;
        }
    }

    /// <summary>A judge that passes questions on and records the answers by similarity.</summary>
    public IClusterJudge Wrap(IClusterJudge inner) => new BandJudge(inner, this);

    /// <summary>Prints the report; with a referee, grades a sample of members of clusters of three or more.</summary>
    public async Task PrintAsync(
        Clusterer clusterer, IReadOnlyDictionary<string, string> texts, IReadOnlyDictionary<string, string> labels,
        string? refereeSpec, string labelerSpec, int fitSample, int parallel, int seed, CancellationToken cancellationToken)
    {
        var clusters = new List<(StoredCluster Cluster, List<string> Members)>();
        await foreach (var c in clusterer.ListClustersAsync(cancellationToken).ConfigureAwait(false))
        {
            if (c.Count > 0)
            {
                var stored = (await clusterer.Store.GetClustersAsync([c.Id], cancellationToken).ConfigureAwait(false))[0];
                clusters.Add((stored, [.. await clusterer.GetMembersAsync(c.Id, cancellationToken).ConfigureAwait(false)]));
            }
        }

        var items = clusters.Sum(c => c.Members.Count);
        var large = clusters.Where(c => c.Members.Count >= CoverageSize).ToList();
        var covered = large.Sum(c => c.Members.Count);
        Console.WriteLine($"coverage: {(double)covered / items:P1} of {items} items in {large.Count} clusters of {CoverageSize}+; {clusters.Count} clusters, {clusters.Count(c => c.Members.Count == 1)} singletons");

        // Purity on the labels' first part (the product, for the complaints): a cluster that mixes them has misplaced items.
        static string Coarse(string label) => label.Split(" | ")[0];
        var mixed = large.Sum(c => c.Members.Count - c.Members.GroupBy(m => Coarse(labels[m])).Max(g => g.Count()));
        Console.WriteLine($"coarse-label purity in clusters of {CoverageSize}+: {1 - ((double)mixed / Math.Max(1, covered)):P1}");

        await DuplicatesAsync(clusterer, large, clusterer.Options, cancellationToken).ConfigureAwait(false);
        PrintBands();

        var paths = clusters.SelectMany(c => c.Members.Select(m => (Item: m, Path: Path(m, c.Cluster.Id)))).ToDictionary(p => p.Item, p => p.Path, StringComparer.Ordinal);
        Console.WriteLine("placement paths, all items: " + string.Join(", ", paths.Values.GroupBy(p => p).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key} {g.Count()}")));
        Console.WriteLine($"join questions per item: {(double)_joinQuestions.Values.Sum() / items:0.00}, merge questions per item: {(double)_answers.Count(a => a.Kind == "merge") / items:0.00}");

        if (refereeSpec is null)
        {
            return;
        }

        // A sample of members of clusters of 3+, graded against their cluster's title.
        var random = new Random(seed + 11);
        var sample = large.SelectMany(c => c.Members.Select(m => (Item: m, c.Cluster))).OrderBy(_ => random.Next()).Take(fitSample).ToList();
        var textStore = new InMemoryItemTextStore();
        foreach (var (id, text) in texts)
        {
            textStore.Set(id, text);
        }

        using var labelChat = Models.Chat(labelerSpec);
        var labelOptions = Models.ChatOptions(labelerSpec);
        var labeler = new ChatClientLabeler(labelChat, textStore, new ChatClientLabelerOptions { ChatOptions = labelOptions, UseZeroTemperature = Models.AcceptsTemperature(labelerSpec) });
        var titles = new Dictionary<long, string>();
        foreach (var cluster in sample.Select(s => s.Cluster).DistinctBy(c => c.Id).OrderBy(c => c.Id))
        {
            var examples = await clusterer.Store.GetExamplesAsync(cluster.Id, 10, cancellationToken).ConfigureAwait(false);
            titles[cluster.Id] = await labeler.WriteLabelAsync([.. examples.Select(e => texts[e])], cancellationToken).ConfigureAwait(false) ?? "(no title)";
        }

        using var referee = Models.Chat(refereeSpec);
        var refereeOptions = Models.ChatOptions(refereeSpec);
        refereeOptions.Temperature = Models.AcceptsTemperature(refereeSpec) ? 0 : null;
        var grades = new (string Path, bool? Fits)[sample.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, sample.Count), new ParallelOptions { MaxDegreeOfParallelism = parallel, CancellationToken = cancellationToken }, async (i, ct) =>
        {
            var (item, cluster) = sample[i];
            var response = await referee.GetResponseAsync(
                [
                    new ChatMessage(ChatRole.System, RefereeInstructions),
                    new ChatMessage(ChatRole.User, $"Page title: {titles[cluster.Id]}\nStatement: {texts[item]}"),
                ], refereeOptions, ct).ConfigureAwait(false);
            var answer = System.Text.RegularExpressions.Regex.Replace(response.Text, "<think>.*?(</think>|$)", " ", System.Text.RegularExpressions.RegexOptions.Singleline).Trim().TrimStart('*', '"').ToUpperInvariant();
            grades[i] = (paths[item], answer.StartsWith("YES", StringComparison.Ordinal) ? true : answer.StartsWith("NO", StringComparison.Ordinal) ? false : null);
        }).ConfigureAwait(false);

        var graded = grades.Where(g => g.Fits is not null).ToList();
        Console.WriteLine($"fit (referee {Models.ModelName(refereeSpec)}, titles by {Models.ModelName(labelerSpec)}): {Rate(graded)} of {graded.Count} graded ({grades.Length - graded.Count} unreadable)");
        foreach (var group in graded.GroupBy(g => g.Path).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"  {group.Key,-24} {Rate([.. group])}  n={group.Count()}");
        }

        static string Rate(List<(string Path, bool? Fits)> graded) =>
            graded.Count == 0 ? "-" : ((double)graded.Count(g => g.Fits == true) / graded.Count).ToString("P0", CultureInfo.InvariantCulture);
    }

    public const string RefereeInstructions =
        "You check a page that groups people's need statements under a title. You are given the page's title and one statement filed on it. " +
        "Would the title honestly describe this statement: is the statement about what the title says, not merely about a related topic? " +
        "Answer with one word: yes or no.";

    // How an item came to be where it is now.
    private string Path(string item, long finalCluster)
    {
        var questions = _joinQuestions.GetValueOrDefault(item);
        if (!_placed.TryGetValue(item, out var placed))
        {
            return "unknown";
        }

        if (placed.Cluster != finalCluster)
        {
            return "moved by a merge";
        }

        return placed.Reason switch
        {
            PlacementReason.NewCluster => questions == 0 ? "founder, never asked" : "founder, after refusals",
            PlacementReason.Similarity => questions == 0 ? "auto-joined" : "auto-joined after refusals",
            _ => questions <= 1 ? "joined on question 1" : "joined on question 2+",
        };
    }

    // Live clusters of 3+ that look like one: pairs at high centroid similarity, and why they stayed apart.
    private static async Task DuplicatesAsync(Clusterer clusterer, List<(StoredCluster Cluster, List<string> Members)> large, ClusteringOptions options, CancellationToken cancellationToken)
    {
        var reasons = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var pairs = 0;
        for (var i = 0; i < large.Count; i++)
        {
            var a = large[i].Cluster;
            for (var j = i + 1; j < large.Count; j++)
            {
                var b = large[j].Cluster;
                var similarity = Vectors.Dot(a.Centroid.ToArray(), b.Centroid.ToArray());
                if (similarity < 0.84)
                {
                    continue;
                }

                pairs++;
                var checks = await clusterer.Store.GetMergeChecksAsync([MergeCheck.Key(a.Id, b.Id)], cancellationToken).ConfigureAwait(false);
                var refused = a.RefusedBy.Contains(b.Id) || b.RefusedBy.Contains(a.Id);
                var reason = checks.Count > 0 ? $"judged {checks[0].Outcome}"
                    : refused ? "refused, never asked"
                    : similarity < options.MergeCheckSimilarity ? "below merge check"
                    : "never asked";
                var key = $"{(similarity >= 0.88 ? "0.88+" : "0.84-0.88")} {reason}";
                reasons[key] = reasons.GetValueOrDefault(key) + 1;
            }
        }

        Console.WriteLine($"near-duplicate pairs of clusters of {CoverageSize}+ (centroid similarity 0.84 or more): {pairs}" + (pairs == 0 ? "" : ": " + string.Join(", ", reasons.Select(r => $"{r.Key} {r.Value}"))));

        // The real-data gate's measures: duplicates are pairs at centroid 0.88 or more whose
        // members are as close across the pair as within it (cross at least 0.95 of the mean of
        // the two within-cluster averages); cohesion is |sum| / size.
        static double Dot(ReadOnlySpan<double> x, ReadOnlySpan<double> y) => System.Numerics.Tensors.TensorPrimitives.Dot(x, y);
        static double Within(StoredCluster c) => (Dot(c.Sum.Span, c.Sum.Span) - c.Count) / ((double)c.Count * (c.Count - 1));
        var duplicates = 0;
        for (var i = 0; i < large.Count; i++)
        {
            for (var j = i + 1; j < large.Count; j++)
            {
                var (a, b) = (large[i].Cluster, large[j].Cluster);
                var cross = Dot(a.Sum.Span, b.Sum.Span) / ((double)a.Count * b.Count);
                if (Vectors.Dot(a.Centroid.ToArray(), b.Centroid.ToArray()) >= 0.88 && cross >= 0.95 * (Within(a) + Within(b)) / 2)
                {
                    duplicates++;
                }
            }
        }

        var cohesion = large.Select(c => Math.Sqrt(Dot(c.Cluster.Sum.Span, c.Cluster.Sum.Span)) / c.Cluster.Count).Order().ToList();
        Console.WriteLine(cohesion.Count == 0 ? "duplicates (gate): 0" : string.Create(
            CultureInfo.InvariantCulture,
            $"duplicates (gate: centroid 0.88+, cross >= 0.95 x within): {duplicates}; cohesion of clusters of {CoverageSize}+: median {cohesion[cohesion.Count / 2]:0.000}, p10 {cohesion[cohesion.Count / 10]:0.000}"));
    }

    private void PrintBands()
    {
        foreach (var kind in new[] { "join, first question", "join, later question", "merge" })
        {
            var answers = _answers.Where(a => a.Kind == kind).ToList();
            if (answers.Count == 0)
            {
                continue;
            }

            var bands = ThresholdCalibratorBands(answers);
            Console.WriteLine($"{kind} yes-rate by similarity ({answers.Count} answered): " + string.Join("  ", bands.Select(b => $"{b.From:0.00}:{b.Yes}/{b.Questions}")));
        }

        static List<SimilarityBand> ThresholdCalibratorBands(List<(string Kind, double Similarity, bool Yes)> answers) =>
            [.. answers.GroupBy(a => (int)Math.Floor((a.Similarity + 1e-9) / 0.02)).OrderBy(g => g.Key)
                .Select(g => new SimilarityBand(g.Key * 0.02, (g.Key + 1) * 0.02, g.Count(), g.Count(a => a.Yes)))];
    }

    private sealed class BandJudge(IClusterJudge inner, RunReport report) : IClusterJudge
    {
        public async Task<IReadOnlyList<Verdict>> JudgeAsync(IReadOnlyList<Question> questions, CancellationToken cancellationToken)
        {
            var verdicts = await inner.JudgeAsync(questions, cancellationToken).ConfigureAwait(false);
            lock (report._lock)
            {
                var byId = verdicts.GroupBy(v => v.QuestionId).ToDictionary(g => g.Key, g => g.First());
                foreach (var question in questions)
                {
                    if (question is JoinQuestion asked)
                    {
                        report._firstJoin.TryAdd(asked.ItemId, asked.Id);
                    }

                    if (!byId.TryGetValue(question.Id, out var verdict) || verdict.Kind is not (VerdictKind.Same or VerdictKind.Different) || !report._answered.Add(question.Id))
                    {
                        continue;
                    }

                    var yes = verdict.Kind == VerdictKind.Same;
                    switch (question)
                    {
                        case JoinQuestion join:
                            report._answers.Add((report._firstJoin[join.ItemId] == join.Id ? "join, first question" : "join, later question", join.Similarity, yes));
                            break;
                        case MergeQuestion merge:
                            report._answers.Add(("merge", merge.Similarity, yes));
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
