using System.Globalization;
using System.Reflection;

namespace Akinly.Evaluation;

/// <summary>The commands: <c>calibrate</c>, <c>evaluate</c>, and <c>experiment</c> for development.</summary>
public static class Cli
{
    public const string Usage = """
        akinly-eval calibrate --dataset banking77|clinc150|massive|intents|news-t|tweets-t|complaints|file.jsonl [--embedder ollama:bge-m3] [--sample 3000]
                              [--loss 0.01] [--max-checks 3] [--seed 1] [--cache .eval-cache] [--out docs/evaluation]
        akinly-eval evaluate  --dataset banking77|clinc150|massive|intents|news-t|tweets-t|complaints|file.jsonl [--embedder ollama:bge-m3] [--judge ollama:qwen3.5:9b,anthropic:claude-haiku-5-5]
                              [--sample 0] [--calibration-sample 3000] [--orders 3] [--judged-orders 3]
                              [--join J --check C --merge M --merge-check MC] [--batch 50] [--parallel 4]
                              [--seed 1] [--cache .eval-cache] [--out docs/evaluation]
        akinly-eval experiment --dataset ... [--split train|test] [--dev 3000|0] [--judge oracle|none|provider:model]
                              [--calibrate-on dataset] [--order shuffle|file] [--prefer balanced|precision|recall]
                              [--thresholds calibrated|defaults] [--merge-linkage average|centroid] [--set Option=value,...]
                              [--probe N] [--dump file]
                              [--referee provider:model [--labeler provider:model] [--fit-sample 300]] [--calibrate-floors 1500] [--merge-members 1]
                              Development runs: by default on a sample of the training split, never the test split.
        akinly-eval prepare-complaints [--writer ollama:qwen3.5:9b] [--count 8000] [--parallel 4] [--seed 1] [--cache .eval-cache]
                              Writes the realistic set (dataset "complaints"): need statements on one template, written
                              by a local model from public CFPB complaint narratives.

        Models are provider:model: ollama (a local Ollama at OLLAMA_HOST), openai (OPENAI_API_KEY) or
        anthropic (ANTHROPIC_API_KEY, judges only). Several judges, separated by commas, give a row each.
        The public datasets are downloaded into --cache from a fixed commit, checked against their
        SHA-256, and never committed. calibrate measures their training split; evaluate clusters
        their test split (--sample 0: all of it) at thresholds calibrated on the training split,
        unless all four thresholds are given. A file of {"id", "text", "label"} lines is split in
        two: --sample texts (default half) are evaluated, the rest calibrate.
        """;

    private const int MinimumCalibrationTexts = 100;

    public static async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var arguments = Arguments.Parse(args);
        switch (arguments.Command)
        {
            case null or "help" or "--help" or "-h":
                Console.WriteLine(Usage);
                return 0;
            case "calibrate":
                arguments.Allow("dataset", "embedder", "sample", "loss", "max-checks", "seed", "cache", "out");
                await CalibrateAsync(arguments, cancellationToken).ConfigureAwait(false);
                return 0;
            case "evaluate":
                arguments.Allow("dataset", "embedder", "judge", "sample", "calibration-sample", "orders", "judged-orders",
                    "join", "check", "merge", "merge-check", "batch", "parallel", "seed", "cache", "out");
                await EvaluateAsync(arguments, cancellationToken).ConfigureAwait(false);
                return 0;
            case "experiment":
                arguments.Allow("dataset", "embedder", "judge", "seed", "cache", "batch", "parallel", "split", "dev", "order", "calibrate-on",
                    "loss", "calibrate-checks", "merge-linkage", "prefer", "thresholds", "set", "probe", "dump", "criterion", "join-prompt", "choice-prompt",
                    "merge-prompt", "think", "sweep-step", "referee", "labeler", "fit-sample", "calibrate-floors", "merge-members");
                await Experiment.RunAsync(arguments, cancellationToken).ConfigureAwait(false);
                return 0;
            case "prepare-complaints":
                arguments.Allow("writer", "count", "parallel", "seed", "cache");
                await Complaints.PrepareAsync(arguments, cancellationToken).ConfigureAwait(false);
                return 0;
            default:
                throw new ArgumentException($"Unknown command '{arguments.Command}'. Run without arguments for the usage.");
        }
    }

    private static async Task CalibrateAsync(Arguments arguments, CancellationToken cancellationToken)
    {
        var (dataset, embedder, seed, cache, output) = Common(arguments);
        var texts = Dataset.IsPublic(dataset)
            ? await Dataset.LoadAsync(dataset, "train", cache, cancellationToken).ConfigureAwait(false)
            : Dataset.LoadJsonLines(dataset);
        var sample = Dataset.Sample(texts, arguments.Number("sample", 3000), seed);
        if (sample.Count < MinimumCalibrationTexts)
        {
            throw new ArgumentException($"{sample.Count} texts are too few to calibrate on: at least {MinimumCalibrationTexts} are needed.");
        }

        var vectors = await Embeddings.GetAsync(sample, embedder, cache, cancellationToken).ConfigureAwait(false);
        var calibration = Calibrator.Run(
            Describe(dataset, "train"), embedder, sample, vectors,
            loss: double.Parse(arguments.Get("loss", "0.01"), CultureInfo.InvariantCulture), seed: seed,
            maxChecks: arguments.Number("max-checks", new ClusteringOptions().MaxChecksPerItem, minimum: 1));
        var markdown = Calibrator.Markdown(calibration);
        await ReportWriter.WriteAsync(output, $"calibration-{Embeddings.Safe(Dataset.Name(dataset))}-{Embeddings.Safe(embedder)}", calibration, markdown, cancellationToken).ConfigureAwait(false);
        Console.WriteLine(markdown);
    }

    private static async Task EvaluateAsync(Arguments arguments, CancellationToken cancellationToken)
    {
        var (dataset, embedder, seed, cache, output) = Common(arguments);
        string[] judgeSpecs = arguments.Has("judge") ? [.. arguments.Get("judge", "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)] : [];
        foreach (var spec in judgeSpecs)
        {
            Models.Check(spec);
        }

        var orders = arguments.Number("orders", 3, minimum: 1);
        var judgedOrders = arguments.Number("judged-orders", 3, minimum: 1);
        var batch = arguments.Number("batch", 50, minimum: 1);
        var parallel = arguments.Number("parallel", 4, minimum: 1);
        var given = Given(arguments);

        // The texts to cluster, and those to calibrate on: another split, or the rest of a file.
        IReadOnlyList<LabeledText> texts;
        IReadOnlyList<LabeledText> calibrationTexts;
        if (Dataset.IsPublic(dataset) && !Dataset.HasSplit(dataset) && given is null)
        {
            throw new ArgumentException(
                $"{dataset} has no training split apart from its test split, so calibrating on it would tune on the texts measured: give all four thresholds, or calibrate on another dataset with the experiment command (--calibrate-on).");
        }

        if (Dataset.IsPublic(dataset))
        {
            texts = Dataset.Sample(await Dataset.LoadAsync(dataset, "test", cache, cancellationToken).ConfigureAwait(false), arguments.Number("sample", 0), seed);
            calibrationTexts = given is null
                ? Dataset.Sample(await Dataset.LoadAsync(dataset, "train", cache, cancellationToken).ConfigureAwait(false), arguments.Number("calibration-sample", 3000), seed + 1)
                : [];
        }
        else
        {
            var all = Dataset.LoadJsonLines(dataset);
            texts = Dataset.Sample(all, arguments.Number("sample", all.Count / 2, minimum: 1), seed);
            var evaluated = texts.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
            calibrationTexts = [.. all.Where(t => !evaluated.Contains(t.Id))];
        }

        if (given is null && calibrationTexts.Count < MinimumCalibrationTexts)
        {
            throw new ArgumentException(
                $"Only {calibrationTexts.Count} texts are left to calibrate on (at least {MinimumCalibrationTexts} are needed): calibrate on more, or give all four thresholds.");
        }

        var model = Models.ModelName(embedder);
        var vectors = await Embeddings.GetAsync(texts, embedder, cache, cancellationToken).ConfigureAwait(false);
        Calibration? calibration = null;
        var options = new ClusteringOptions { EmbeddingModel = model };
        if (given is { } thresholds)
        {
            (options.JoinSimilarity, options.CheckSimilarity, options.MergeSimilarity, options.MergeCheckSimilarity) = thresholds;
        }
        else
        {
            var calibrationVectors = await Embeddings.GetAsync(calibrationTexts, embedder, cache, cancellationToken).ConfigureAwait(false);
            calibration = Calibrator.Run(Describe(dataset, "train"), embedder, calibrationTexts, calibrationVectors, seed: seed);
            (options.JoinSimilarity, options.CheckSimilarity, options.MergeSimilarity, options.MergeCheckSimilarity) =
                (calibration.JoinSimilarity, calibration.CheckSimilarity, calibration.MergeSimilarity, calibration.MergeCheckSimilarity);
        }

        var results = new List<SetupResult>();
        var warnings = new List<string>(calibration?.Warnings ?? []);
        var name = $"results-{Embeddings.Safe(Dataset.Name(dataset))}-{Embeddings.Safe(embedder)}";
        var report = new EvaluationReport(
            Version(), DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Dataset.Name(dataset),
            Describe(dataset, "test"), Dataset.HasSingleSource(dataset) ? Dataset.Source(dataset, "test") : null,
            texts.Count, texts.Select(t => t.Label).Distinct(StringComparer.Ordinal).Count(), embedder,
            given is not null ? "given" : Dataset.IsPublic(dataset) ? "calibrated on the training split (the same groups, other texts)" : "calibrated on the other texts of the file",
            calibration, seed, orders, judgeSpecs.Length == 0 ? 0 : judgedOrders, batch, parallel, results, warnings);
        Task SaveAsync() => ReportWriter.WriteAsync(output, name, report, ReportWriter.Markdown(report), cancellationToken);
        IReadOnlyList<LabeledText> Order(int o) => Evaluator.Shuffle(texts, seed + 100 + o);

        // A reference, not a competitor: k-means told the true number of groups, over all the texts
        // at once. It shows how far these embeddings go when the answer's size is known.
        Console.Error.WriteLine("k-means with the true number of groups");
        var groups = texts.Select(t => t.Label).Distinct(StringComparer.Ordinal).Count();
        var kmeansRuns = new List<Scores>();
        var kmeansSeconds = 0.0;
        for (var o = 0; o < orders; o++)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var assigned = KMeans.Cluster([.. texts.Select(t => vectors[t.Id])], groups, seed + 100 + o);
            kmeansSeconds += watch.Elapsed.TotalSeconds;
            kmeansRuns.Add(Metrics.Score([.. texts.Select(t => t.Label)], [.. assigned.Select(a => (long)a)]));
        }

        results.Add(new SetupResult("k-means, told the number of groups (reference)", $"k = {groups}, all texts at once", kmeansRuns, kmeansSeconds / orders));
        await SaveAsync().ConfigureAwait(false);

        // Cosine only, at the threshold that scores best on these texts' own labels: an
        // optimistic baseline, since in practice there are no labels to tune on.
        Console.Error.WriteLine("cosine only: choosing the best threshold on the labels");
        var best = (Threshold: 0.0, Ari: double.NegativeInfinity);
        for (var step = 40; step <= 98; step++)
        {
            var threshold = step / 100.0;
            var (scores, _) = await Evaluator.CosineAsync(Order(0), vectors, ClusteringOptions.CosineOnly(model, threshold), batch, cancellationToken).ConfigureAwait(false);
            best = scores.AdjustedRand > best.Ari ? (threshold, scores.AdjustedRand) : best;
        }

        if (best.Threshold is 0.40 or 0.98)
        {
            warnings.Add($"The best cosine-only threshold, {best.Threshold:0.00}, is at the edge of the range searched (0.40 to 0.98).");
        }

        results.Add(await CosineSetupAsync("Cosine only, best threshold on the labels", $"join and merge {best.Threshold:0.00}", ClusteringOptions.CosineOnly(model, best.Threshold)).ConfigureAwait(false));
        await SaveAsync().ConfigureAwait(false);

        // Cosine only at the calibrated (or given) join and merge thresholds: no judge, no tuning on these labels.
        results.Add(await CosineSetupAsync(
            given is null ? "Cosine only, calibrated thresholds" : "Cosine only, given thresholds",
            $"join {options.JoinSimilarity:0.00#}, merge {options.MergeSimilarity:0.00#}",
            ClusteringOptions.CosineOnly(model, options.JoinSimilarity, options.MergeSimilarity)).ConfigureAwait(false));
        await SaveAsync().ConfigureAwait(false);

        foreach (var judgeSpec in judgeSpecs)
        {
            using var chat = new CountingChatClient(Models.Chat(judgeSpec));
            var judge = new CountingJudge(Evaluator.Judge(chat, texts, Models.ChatOptions(judgeSpec), parallel, Models.AcceptsTemperature(judgeSpec)));
            var runs = new List<Scores>();
            var seconds = 0.0;
            for (var o = 0; o < judgedOrders; o++)
            {
                Console.Error.WriteLine($"judged by {judgeSpec}, order {o + 1} of {judgedOrders}");
                var (scores, elapsed) = await Evaluator.JudgedAsync(
                    Order(o), vectors, options, judge, batch,
                    (done, at) => Console.Error.Write($"\r  {done}/{texts.Count} items, {judge.Verdicts} verdicts, {TimeSpan.FromSeconds(at):hh\\:mm\\:ss}"),
                    cancellationToken).ConfigureAwait(false);
                Console.Error.WriteLine();
                runs.Add(scores);
                seconds += elapsed;
            }

            var items = (double)texts.Count * judgedOrders;
            var failed = judge.Verdicts == 0 ? 0 : (double)judge.Failed / judge.Verdicts;
            if (failed > 0.05)
            {
                warnings.Add($"{failed:P0} of {Models.ModelName(judgeSpec)}'s answers failed and were decided by similarity: its row is partly cosine-only.");
            }

            results.Add(new SetupResult(
                $"Akinly, judged by {Models.ModelName(judgeSpec)}",
                $"join {options.JoinSimilarity:0.00#}, check {options.CheckSimilarity:0.00#}, merge {options.MergeSimilarity:0.00#}, merge check {options.MergeCheckSimilarity:0.00#}",
                runs,
                seconds / judgedOrders,
                new JudgeUsage(judgeSpec, judge.Verdicts / items, chat.InputTokens / items, chat.OutputTokens / items,
                    failed, judge.Verdicts == 0 ? 0 : (double)judge.Unanswerable / judge.Verdicts, chat.Errors)));
            await SaveAsync().ConfigureAwait(false);
        }

        Console.WriteLine(ReportWriter.Markdown(report));

        async Task<SetupResult> CosineSetupAsync(string setup, string settings, ClusteringOptions cosine)
        {
            var runs = new List<Scores>();
            var seconds = 0.0;
            for (var o = 0; o < orders; o++)
            {
                var (scores, elapsed) = await Evaluator.CosineAsync(Order(o), vectors, cosine, batch, cancellationToken).ConfigureAwait(false);
                runs.Add(scores);
                seconds += elapsed;
            }

            return new SetupResult(setup, settings, runs, seconds / orders);
        }
    }

    private static (string Dataset, string Embedder, int Seed, string Cache, string Output) Common(Arguments arguments)
    {
        var dataset = arguments.Get("dataset", "banking77");
        if (!Dataset.IsPublic(dataset) && !File.Exists(dataset))
        {
            throw new ArgumentException($"'{dataset}' is neither a public dataset (banking77, clinc150, massive, intents, news-t, tweets-t, complaints) nor an existing JSON-lines file.");
        }

        var embedder = arguments.Get("embedder", "ollama:bge-m3");
        Models.Check(embedder);
        return (dataset, embedder, arguments.Number("seed", 1), arguments.Get("cache", ".eval-cache"), arguments.Get("out", Path.Combine("docs", "evaluation")));
    }

    /// <summary>All four thresholds, if given; one without the others is an error.</summary>
    private static (double Join, double Check, double Merge, double MergeCheck)? Given(Arguments arguments)
    {
        string[] names = ["join", "check", "merge", "merge-check"];
        var count = names.Count(arguments.Has);
        if (count == 0)
        {
            return null;
        }

        if (count < names.Length)
        {
            throw new ArgumentException("Give all four of --join, --check, --merge and --merge-check, or none to calibrate them.");
        }

        var thresholds = (arguments.Similarity("join"), arguments.Similarity("check"), arguments.Similarity("merge"), arguments.Similarity("merge-check"));
        return thresholds.Item2 <= thresholds.Item1 && thresholds.Item4 <= thresholds.Item3
            ? thresholds
            : throw new ArgumentException("--check must be at most --join, and --merge-check at most --merge.");
    }

    private static string Describe(string dataset, string split) =>
        Dataset.IsPublic(dataset) ? $"{Dataset.License(dataset)}; the {split} split" : Dataset.Name(dataset);

    /// <summary>The Akinly version measured, without the commit: a working tree's commit may never reach the repository.</summary>
    private static string Version() =>
        typeof(Clusterer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "unknown";
}
