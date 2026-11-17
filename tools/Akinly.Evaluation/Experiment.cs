using System.Globalization;
using Akinly.Storage;

namespace Akinly.Evaluation;

/// <summary>
/// Development runs on a held-out part of the training split (never the test split): scores,
/// how right the judge was, and why the clusters of one group stayed apart.
/// </summary>
public static class Experiment
{
    public static async Task RunAsync(Arguments arguments, CancellationToken cancellationToken)
    {
        var dataset = arguments.Get("dataset", "banking77");
        var embedder = arguments.Get("embedder", "ollama:bge-m3");
        var seed = arguments.Number("seed", 1);
        var cache = arguments.Get("cache", ".eval-cache");
        var judgeSpec = arguments.Get("judge", "oracle");
        var batch = arguments.Number("batch", 50, minimum: 1);
        var parallel = arguments.Number("parallel", 4, minimum: 1);

        // Dev: a stratified sample of the training split (--split test and --dev 0 for a final
        // run on the whole test split); calibration: another sample of the rest of the training
        // split, or of another dataset's (--calibrate-on), so nothing is tuned on the texts measured.
        var split = arguments.Get("split", "train");
        var all = await Dataset.LoadAsync(dataset, split, cache, cancellationToken).ConfigureAwait(false);
        var texts = Dataset.Sample(all, arguments.Number("dev", 3000), seed + 7);
        var dev = texts.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        if (arguments.Get("order", "shuffle") == "file" && texts.Count != all.Count)
        {
            throw new ArgumentException("--order file keeps the file's order only with --dev 0: a sample is shuffled.");
        }

        var calibrateOn = arguments.Get("calibrate-on", dataset);
        var calibrationPool = calibrateOn == dataset
            ? (split == "train" ? all : await Dataset.LoadAsync(dataset, "train", cache, cancellationToken).ConfigureAwait(false)).Where(t => !dev.Contains(t.Id)).ToList()
            : [.. await Dataset.LoadAsync(calibrateOn, "train", cache, cancellationToken).ConfigureAwait(false)];
        if (calibrationPool.Count == 0)
        {
            throw new ArgumentException($"Nothing of {dataset} is left to calibrate on: use --calibrate-on with another dataset.");
        }

        var calibrationTexts = Dataset.Sample(calibrationPool, 3000, seed + 1);

        var model = Models.ModelName(embedder);
        var vectors = await Embeddings.GetAsync(texts, embedder, cache, cancellationToken).ConfigureAwait(false);
        var calibrationVectors = await Embeddings.GetAsync(calibrationTexts, embedder, cache, cancellationToken).ConfigureAwait(false);
        var linkage = Enum.Parse<ClusterLinkage>(arguments.Get("merge-linkage", "average"), ignoreCase: true);
        var calibration = Calibrator.Run(calibrateOn, embedder, calibrationTexts, calibrationVectors, loss: double.Parse(arguments.Get("loss", "0.01"), CultureInfo.InvariantCulture), seed: seed, maxChecks: arguments.Number("calibrate-checks", 5, minimum: 1), linkage: linkage);
        // --prefer precision|recall: that preference's calibrated thresholds and the judge's
        // matching tie-break. --thresholds defaults: the library's defaults, no calibration.
        var prefer = Enum.Parse<Akinly.AI.ClusteringPreference>(arguments.Get("prefer", "balanced"), ignoreCase: true);
        var set = prefer switch
        {
            Akinly.AI.ClusteringPreference.Precision => calibration.Precision,
            Akinly.AI.ClusteringPreference.Recall => calibration.Recall,
            _ => new ThresholdSet(calibration.JoinSimilarity, calibration.CheckSimilarity, calibration.MergeSimilarity, calibration.MergeCheckSimilarity),
        };
        var options = arguments.Get("thresholds", "calibrated") == "defaults"
            ? new ClusteringOptions { EmbeddingModel = model, MergeLinkage = linkage }
            : new ClusteringOptions
            {
                EmbeddingModel = model,
                MergeLinkage = linkage,
                JoinSimilarity = set.JoinSimilarity,
                CheckSimilarity = set.CheckSimilarity,
                MergeSimilarity = set.MergeSimilarity,
                MergeCheckSimilarity = set.MergeCheckSimilarity,
            };
        foreach (var setting in arguments.Get("set", "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var equals = setting.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                throw new ArgumentException($"--set takes Option=value pairs, not '{setting}'.");
            }

            var (name, value) = (setting[..equals], setting[(equals + 1)..]);
            var property = typeof(ClusteringOptions).GetProperty(name) ?? throw new ArgumentException($"No option {name}.");
            property.SetValue(options, property.PropertyType.IsEnum
                ? Enum.Parse(property.PropertyType, value, ignoreCase: true)
                : Convert.ChangeType(value, property.PropertyType, CultureInfo.InvariantCulture));
        }

        Console.WriteLine($"{dataset} {split} {texts.Count} texts, thresholds {arguments.Get("thresholds", "calibrated")} on {calibrateOn}, prefer {prefer}, {texts.Select(t => t.Label).Distinct().Count()} groups, {embedder}, judge {judgeSpec}");
        Console.WriteLine($"join {options.JoinSimilarity} check {options.CheckSimilarity} merge {options.MergeSimilarity} merge-check {options.MergeCheckSimilarity} max-checks {options.MaxChecksPerItem} choices {options.JoinChoices} candidates {options.CandidateClusters} refusals {options.MergeRefusals} linkage {options.MergeLinkage}");

        var labels = texts.ToDictionary(t => t.Id, t => t.Label, StringComparer.Ordinal);
        if (arguments.Has("probe"))
        {
            await ProbeAsync(arguments, texts, vectors, options, labels, judgeSpec, parallel, batch, seed, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Stream sets keep their file order (--order file); otherwise a seeded shuffle.
        var order = arguments.Get("order", "shuffle") == "file" ? texts : Evaluator.Shuffle(texts, seed + 100);
        if (judgeSpec == "none")
        {
            // References without a judge: cosine only at the calibrated thresholds, cosine only at
            // the threshold best on these labels (optimistic), and k-means told the true count.
            var (calibrated, calibratedSeconds) = await Evaluator.CosineAsync(order, vectors, ClusteringOptions.CosineOnly(model, options.JoinSimilarity, options.MergeSimilarity), batch, cancellationToken).ConfigureAwait(false);
            Print("cosine, calibrated", calibrated, calibratedSeconds);
            var best = (Threshold: 0.0, Scores: (Scores?)null);
            for (var step = 40; step <= 98; step += arguments.Number("sweep-step", 2, minimum: 1))
            {
                try
                {
                    var (tried, _) = await Evaluator.CosineAsync(order, vectors, ClusteringOptions.CosineOnly(model, step / 100.0), batch, cancellationToken).ConfigureAwait(false);
                    best = best.Scores is null || tried.AdjustedRand > best.Scores.AdjustedRand ? (step / 100.0, tried) : best;
                }
                catch (InvalidOperationException exception)
                {
                    // A low threshold can chain clusters together without ever settling: skip it.
                    Console.WriteLine($"  cosine {step / 100.0:0.00}: {exception.Message}");
                }
            }

            if (best.Scores is not null)
            {
                Print($"cosine, best on labels ({best.Threshold:0.00})", best.Scores, 0);
            }

            var groups = texts.Select(t => t.Label).Distinct(StringComparer.Ordinal).Count();
            var kmeans = Enumerable.Range(0, 3).Select(o => Metrics.Score([.. texts.Select(t => t.Label)], [.. KMeans.Cluster([.. texts.Select(t => vectors[t.Id])], groups, seed + 100 + o).Select(a => (long)a)])).ToList();
            Print($"k-means, told k = {groups} (mean of 3)", new Scores(
                kmeans.Average(s => s.AdjustedRand), kmeans.Average(s => s.Accuracy), kmeans.Average(s => s.Nmi), kmeans.Average(s => s.Homogeneity), kmeans.Average(s => s.Completeness),
                kmeans.Average(s => s.VMeasure), kmeans.Average(s => s.PairPrecision), kmeans.Average(s => s.PairRecall), kmeans.Average(s => s.PairF1), groups, 0), 0);
            return;
        }

        IClusterJudge inner;
        CountingChatClient? chat = null;
        if (judgeSpec == "oracle")
        {
            inner = new OracleJudge(labels);
        }
        else
        {
            var client = new CountingChatClient(Models.Chat(judgeSpec));
            chat = client;
            inner = Evaluator.Judge(client, texts, Models.ChatOptions(judgeSpec), parallel, Models.AcceptsTemperature(judgeSpec), o => Prompts(arguments, o));
        }

        // --calibrate-floors N: the library's calibrator, with this judge, on N texts of the
        // calibration pool (never the texts measured), sets the two floors.
        if (arguments.Has("calibrate-floors") && chat is not null)
        {
            var pilot = Dataset.Sample(calibrationPool, arguments.Number("calibrate-floors", 1500, minimum: 50), seed + 2);
            var pilotVectors = await Embeddings.GetAsync(pilot, embedder, cache, cancellationToken).ConfigureAwait(false);
            var pilotJudge = Evaluator.Judge(chat, pilot, Models.ChatOptions(judgeSpec), parallel, Models.AcceptsTemperature(judgeSpec), o => Prompts(arguments, o));
            var floors = await ThresholdCalibrator.CalibrateAsync(
                Evaluator.Shuffle(pilot, seed + 3).Select(t => new ClusterItem(t.Id, pilotVectors[t.Id])), pilotJudge, options, cancellationToken: cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"calibrated floors on {pilot.Count} texts: check {floors.CheckSimilarity:0.00}, merge check {floors.MergeCheckSimilarity:0.00}");
            Console.WriteLine("  join: " + string.Join("  ", floors.JoinBands.Select(b => $"{b.From:0.00}:{b.Yes}/{b.Questions}")));
            Console.WriteLine("  merge: " + string.Join("  ", floors.MergeBands.Select(b => $"{b.From:0.00}:{b.Yes}/{b.Questions}")));
            foreach (var warning in floors.Warnings)
            {
                Console.WriteLine("  warning: " + warning);
            }

            options = floors.ApplyTo(options);
        }

        var textsById = texts.ToDictionary(t => t.Id, t => t.Text, StringComparer.Ordinal);
        var recorder = new RecordingJudge(inner, labels, textsById, Dump(arguments.Get("dump", ""), cache));
        var report = new RunReport();
        var clusterer = new Clusterer(new InMemoryClusterStore(), options);
        var (scores, seconds) = await Evaluator.JudgedOnAsync(
            clusterer, order, vectors, report.Wrap(recorder), batch, cancellationToken,
            (done, at) => Console.Error.Write($"\r  {done}/{order.Count} items, {TimeSpan.FromSeconds(at):hh\\:mm\\:ss}"), report.Observe).ConfigureAwait(false);
        Console.Error.WriteLine();
        chat?.Dispose();

        Print($"akinly, judge {judgeSpec}", scores, seconds);
        if (chat is not null)
        {
            Console.WriteLine($"  judge tokens: {chat.InputTokens:N0} in, {chat.OutputTokens:N0} out");
        }

        recorder.Report();
        await DiagnoseAsync(clusterer, labels, options, cancellationToken).ConfigureAwait(false);
        await report.PrintAsync(
            clusterer, textsById, labels, arguments.Has("referee") ? arguments.Get("referee", "") : null,
            arguments.Get("labeler", judgeSpec == "oracle" ? "ollama:gemma4:12b" : judgeSpec), arguments.Number("fit-sample", 300, minimum: 1), parallel, seed, cancellationToken).ConfigureAwait(false);
    }

    // The dump holds dataset texts, which some datasets may not be redistributed: a relative path
    // goes into the cache folder, which is never committed.
    private static string Dump(string path, string cache) =>
        path.Length == 0 || Path.IsPathRooted(path) ? path : Path.Combine(cache, path);

    private static void Print(string setup, Scores s, double seconds) =>
        Console.WriteLine($"{setup,-36} ARI {s.AdjustedRand:0.000}  ACC {s.Accuracy:0.000}  NMI {s.Nmi:0.000}  P {s.PairPrecision:0.000}  R {s.PairRecall:0.000}  clusters {s.Clusters}  singletons {s.Singletons}  {seconds:0}s");

    /// <summary>
    /// The judge alone: questions from a perfect-judge run (pure clusters), half of them rightly
    /// "same", half rightly "different", put to the real judge.
    /// </summary>
    private static async Task ProbeAsync(
        Arguments arguments, IReadOnlyList<LabeledText> texts, Dictionary<string, float[]> vectors, ClusteringOptions options,
        Dictionary<string, string> labels, string judgeSpec, int parallel, int batch, int seed, CancellationToken cancellationToken)
    {
        // The probe measures yes-or-no answers: its perfect judge and its truth look at one cluster.
        options = options.Clone();
        options.JoinChoices = 1;

        var asked = new List<Question>();
        var oracle = new DelegateJudge(q =>
        {
            lock (asked)
            {
                asked.Add(q);
            }

            return Truth(q, labels) ? VerdictKind.Same : VerdictKind.Different;
        });
        var clusterer = new Clusterer(new InMemoryClusterStore(), options);
        await Evaluator.JudgedOnAsync(clusterer, Evaluator.Shuffle(texts, seed + 100), vectors, oracle, batch, cancellationToken).ConfigureAwait(false);

        var count = arguments.Number("probe", 200);
        var random = new Random(seed + 3);
        var chosen = new List<Question>();
        foreach (var kind in new[] { typeof(JoinQuestion), typeof(MergeQuestion) })
        {
            foreach (var truth in new[] { true, false })
            {
                chosen.AddRange(asked.Where(q => q.GetType() == kind && Truth(q, labels) == truth).OrderBy(_ => random.Next()).Take(kind == typeof(JoinQuestion) ? count / 2 : count / 4));
            }
        }

        var chatOptions = Models.ChatOptions(judgeSpec);
        if (arguments.Has("think"))
        {
            chatOptions.RawRepresentationFactory = _ => new OllamaSharp.Models.Chat.ChatRequest { Think = true };
        }

        using var client = new CountingChatClient(Models.Chat(judgeSpec));
        var store = new InMemoryItemTextStore();
        foreach (var text in texts)
        {
            store.Set(text.Id, text.Text);
        }

        var judgeOptions = new Akinly.AI.ChatClientJudgeOptions { ChatOptions = chatOptions, MaxParallelRequests = parallel, UseZeroTemperature = Models.AcceptsTemperature(judgeSpec) };
        Prompts(arguments, judgeOptions);

        var judge = new Akinly.AI.ChatClientJudge(client, store, judgeOptions);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var verdicts = (await judge.JudgeAsync(chosen, cancellationToken).ConfigureAwait(false)).ToDictionary(v => v.QuestionId);
        var counts = new Dictionary<string, int>();
        foreach (var q in chosen)
        {
            var key = $"{(q is JoinQuestion ? "join" : "merge")} truth {(Truth(q, labels) ? "same" : "diff")} said {(verdicts.TryGetValue(q.Id, out var v) ? v.Kind.ToString() : "none")}";
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        Console.WriteLine($"probe: {chosen.Count} questions of {asked.Count}, {watch.Elapsed.TotalSeconds:0}s, {client.InputTokens / Math.Max(1, chosen.Count)} input tokens each");
        foreach (var (key, n) in counts.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"  {key,-36} {n,6}");
        }

        int Wrong(string kind) => counts.Where(c => c.Key.StartsWith(kind, StringComparison.Ordinal) && (c.Key.Contains("truth same", StringComparison.Ordinal) != c.Key.EndsWith("Same", StringComparison.Ordinal))).Sum(c => c.Value);
        int All(string kind) => counts.Where(c => c.Key.StartsWith(kind, StringComparison.Ordinal)).Sum(c => c.Value);
        Console.WriteLine($"  join error {(double)Wrong("join") / Math.Max(1, All("join")):P0}, merge error {(double)Wrong("merge") / Math.Max(1, All("merge")):P0}");
    }

    /// <summary>The right answer: the item's label against the majority label of the examples.</summary>
    private static bool Truth(Question question, Dictionary<string, string> labels) => question switch
    {
        JoinQuestion j => labels[j.ItemId] == Majority(j.ExampleItemIds, labels),
        MergeQuestion m => Majority(m.ExampleItemIds, labels) == Majority(m.OtherExampleItemIds, labels),
        _ => false,
    };

    // The cluster offered whose majority is the item's group, or null when none is.
    private static long? Right(JoinQuestion question, Dictionary<string, string> labels)
    {
        var label = labels[question.ItemId];
        if (Majority(question.ExampleItemIds, labels) == label)
        {
            return question.ClusterId;
        }

        return question.OtherChoices.FirstOrDefault(c => Majority(c.ExampleItemIds, labels) == label)?.ClusterId;
    }

    // A perfect judge: answers from the labels, picking the right cluster among choices.
    private sealed class OracleJudge(Dictionary<string, string> labels) : IClusterJudge
    {
        public Task<IReadOnlyList<Verdict>> JudgeAsync(IReadOnlyList<Question> questions, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Verdict>>([.. questions.Select(q => q switch
            {
                JoinQuestion { OtherChoices.Count: > 0 } choice => Right(choice, labels) is { } id ? Verdict.Picked(q.Id, id) : Verdict.Different(q.Id),
                _ => Truth(q, labels) ? Verdict.Same(q.Id) : Verdict.Different(q.Id),
            })]);
    }

    private static string? Majority(IEnumerable<string> ids, Dictionary<string, string> labels) =>
        ids.GroupBy(id => labels[id]).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).FirstOrDefault()?.Key;

    // The average similarity of a member of one cluster to a member of the other, from their sums.
    private static double AverageSimilarity(StoredCluster a, StoredCluster b)
    {
        var x = a.Sum.Span;
        var y = b.Sum.Span;
        var dot = 0.0;
        for (var i = 0; i < Math.Min(x.Length, y.Length); i++)
        {
            dot += x[i] * y[i];
        }

        return dot / ((double)a.Count * b.Count);
    }

    private static async Task DiagnoseAsync(Clusterer clusterer, Dictionary<string, string> labels, ClusteringOptions options, CancellationToken cancellationToken)
    {
        var clusters = new List<(StoredCluster Cluster, List<string> Members, string Label)>();
        await foreach (var c in clusterer.ListClustersAsync(cancellationToken).ConfigureAwait(false))
        {
            if (c.Count == 0)
            {
                continue;
            }

            var members = (await clusterer.GetMembersAsync(c.Id, cancellationToken).ConfigureAwait(false)).ToList();
            var stored = (await clusterer.Store.GetClustersAsync([c.Id], cancellationToken).ConfigureAwait(false))[0];
            clusters.Add((stored, members, Majority(members, labels)!));
        }

        // Each group's home: its largest cluster. Every other cluster whose majority is that group is a fragment.
        var home = clusters.GroupBy(c => c.Label).ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.Members.Count).First());
        var reasons = new Dictionary<string, (int Clusters, int Items)>();
        var simBins = new int[10];
        foreach (var c in clusters)
        {
            var h = home[c.Label];
            if (h.Cluster.Id == c.Cluster.Id)
            {
                continue;
            }

            var similarity = Vectors.Dot(c.Cluster.Centroid.ToArray(), h.Cluster.Centroid.ToArray());
            simBins[Math.Clamp((int)(similarity * 10), 0, 9)] += c.Members.Count;
            var linkage = options.MergeLinkage == ClusterLinkage.Average ? AverageSimilarity(c.Cluster, h.Cluster) : similarity;
            var checks = await clusterer.Store.GetMergeChecksAsync([MergeCheck.Key(c.Cluster.Id, h.Cluster.Id)], cancellationToken).ConfigureAwait(false);
            var check = checks.Count > 0 ? checks[0] : null;
            var refused = c.Cluster.RefusedBy.Contains(h.Cluster.Id) || h.Cluster.RefusedBy.Contains(c.Cluster.Id);
            var reason = check is not null ? $"judged {check.Outcome}"
                : refused && options.MergeRefusals == RefusalRule.Block ? "blocked by a refusal"
                : linkage < options.MergeCheckSimilarity ? "below merge check"
                : "not a candidate / other";
            reasons[reason] = reasons.TryGetValue(reason, out var r) ? (r.Clusters + 1, r.Items + c.Members.Count) : (1, c.Members.Count);
        }

        var misplaced = clusters.Sum(c => c.Members.Count(m => labels[m] != c.Label));
        Console.WriteLine($"fragments (clusters, items) by why they stayed apart from their group's largest cluster; impure items: {misplaced}");
        foreach (var (reason, (n, items)) in reasons.OrderByDescending(r => r.Value.Items))
        {
            Console.WriteLine($"  {reason,-28} {n,5} clusters {items,6} items");
        }

        Console.WriteLine("  fragment items by centroid similarity to home: " + string.Join(" ", simBins.Select((n, i) => $"{i / 10.0:0.0}:{n}")));
    }

    /// <summary>Records each question's verdict against the right answer.</summary>
    private sealed class RecordingJudge(IClusterJudge inner, Dictionary<string, string> labels, Dictionary<string, string> texts, string dump) : IClusterJudge
    {
        private readonly Random _random = new(5);
        private readonly Lock _lock = new();
        private readonly Dictionary<string, int> _counts = [];

        public async Task<IReadOnlyList<Verdict>> JudgeAsync(IReadOnlyList<Question> questions, CancellationToken cancellationToken)
        {
            var verdicts = await inner.JudgeAsync(questions, cancellationToken).ConfigureAwait(false);
            var byId = verdicts.ToDictionary(v => v.QuestionId);
            lock (_lock)
            {
                foreach (var q in questions)
                {
                    string key, truth, said;
                    if (q is JoinQuestion { OtherChoices.Count: > 0 } choice)
                    {
                        var right = Right(choice, labels);
                        truth = right is null ? "diff" : "same";
                        said = !byId.TryGetValue(q.Id, out var picked) ? "none"
                            : picked.Kind != VerdictKind.Same ? picked.Kind.ToString()
                            : (picked.PickedClusterId ?? choice.ClusterId) == right ? "Same" : "Wrong";
                        key = $"choice truth {(right is null ? "none" : "in")} said {said}";
                    }
                    else
                    {
                        truth = Truth(q, labels) ? "same" : "diff";
                        said = byId.TryGetValue(q.Id, out var v) ? v.Kind.ToString() : "none";
                        key = $"{(q is JoinQuestion ? "join" : "merge")} truth {truth} said {said}";
                    }

                    _counts[key] = _counts.GetValueOrDefault(key) + 1;
                    if (dump.Length > 0 && q is JoinQuestion join && (truth == "same") != (said == "Same") && _random.Next(20) == 0)
                    {
                        File.AppendAllText(dump, $"--- {key}\nITEM [{labels[join.ItemId]}] {texts[join.ItemId]}\n" +
                            string.Concat(join.ExampleItemIds.Take(3).Select(e => $"   1 [{labels[e]}] {texts[e]}\n")) +
                            string.Concat(join.OtherChoices.SelectMany((c, i) => c.ExampleItemIds.Take(3).Select(e => $"   {i + 2} [{labels[e]}] {texts[e]}\n"))));
                    }
                }
            }

            return verdicts;
        }

        public void Report()
        {
            foreach (var (key, n) in _counts.OrderBy(c => c.Key, StringComparer.Ordinal))
            {
                Console.WriteLine($"  {key,-36} {n,6}");
            }
        }
    }

    // --criterion, --join-prompt, --merge-prompt and --merge-members replace the judge's defaults, for experiments.
    private static void Prompts(Arguments arguments, Akinly.AI.ChatClientJudgeOptions options)
    {
        if (arguments.Has("criterion"))
        {
            options.Criterion = arguments.Get("criterion", "");
        }

        options.Preference = Enum.Parse<Akinly.AI.ClusteringPreference>(arguments.Get("prefer", "balanced"), ignoreCase: true);

        if (arguments.Has("join-prompt"))
        {
            options.JoinInstructions = arguments.Get("join-prompt", "");
        }

        if (arguments.Has("choice-prompt"))
        {
            options.ChoiceInstructions = arguments.Get("choice-prompt", "");
        }

        if (arguments.Has("merge-prompt"))
        {
            options.MergeInstructions = arguments.Get("merge-prompt", "");
        }

        if (arguments.Has("merge-members"))
        {
            options.MergeMembersEachWay = arguments.Number("merge-members", 1, minimum: 0);
        }
    }
}
