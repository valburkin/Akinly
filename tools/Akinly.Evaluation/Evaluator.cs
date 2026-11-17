using System.Diagnostics;
using Akinly.AI;
using Akinly.Storage;

namespace Akinly.Evaluation;

/// <summary>Runs texts through the clusterer, in given orders, and scores the clusters against the labels.</summary>
public static class Evaluator
{
    /// <summary>The most maintenance rounds after the last batch; a clustering still changing then is reported, not hidden.</summary>
    private const int MaxSettleRounds = 50;

    /// <summary>Cosine-only clustering: no judge.</summary>
    public static async Task<(Scores Scores, double Seconds)> CosineAsync(
        IReadOnlyList<LabeledText> order, IReadOnlyDictionary<string, float[]> vectors, ClusteringOptions options, int batchSize, CancellationToken cancellationToken)
    {
        var clusterer = new Clusterer(new InMemoryClusterStore(), options);
        var watch = Stopwatch.StartNew();
        foreach (var batch in order.Chunk(batchSize))
        {
            await clusterer.AddAsync(batch.Select(t => new ClusterItem(t.Id, vectors[t.Id])), cancellationToken).ConfigureAwait(false);
            await clusterer.MaintainAsync(cancellationToken).ConfigureAwait(false);
        }

        await SettleMaintenanceAsync(clusterer, null, cancellationToken).ConfigureAwait(false);
        return (await ScoreAsync(clusterer, order, cancellationToken).ConfigureAwait(false), watch.Elapsed.TotalSeconds);
    }

    /// <summary>Akinly with a judge deciding the close calls.</summary>
    public static async Task<(Scores Scores, double Seconds)> JudgedAsync(
        IReadOnlyList<LabeledText> order, IReadOnlyDictionary<string, float[]> vectors, ClusteringOptions options, IClusterJudge judge,
        int batchSize, Action<int, double>? progress, CancellationToken cancellationToken)
    {
        var clusterer = new Clusterer(new InMemoryClusterStore(), options);
        return await JudgedOnAsync(clusterer, order, vectors, judge, batchSize, cancellationToken, progress).ConfigureAwait(false);
    }

    /// <summary>Akinly with a judge, on a given clusterer, so its clusters can be inspected afterwards.</summary>
    public static async Task<(Scores Scores, double Seconds)> JudgedOnAsync(
        Clusterer clusterer, IReadOnlyList<LabeledText> order, IReadOnlyDictionary<string, float[]> vectors, IClusterJudge judge,
        int batchSize, CancellationToken cancellationToken, Action<int, double>? progress = null, Action<ClusteringStep>? observe = null)
    {
        var watch = Stopwatch.StartNew();
        var done = 0;
        foreach (var batch in order.Chunk(batchSize))
        {
            var step = await clusterer.SettleAsync(batch.Select(t => new ClusterItem(t.Id, vectors[t.Id])), judge, cancellationToken).ConfigureAwait(false);
            step = step.Then(await clusterer.MaintainAsync(cancellationToken).ConfigureAwait(false));
            step = step.Then(await clusterer.SettleAsync(judge, cancellationToken).ConfigureAwait(false));
            observe?.Invoke(step);
            done += batch.Length;
            progress?.Invoke(done, watch.Elapsed.TotalSeconds);
        }

        await SettleMaintenanceAsync(clusterer, judge, cancellationToken, observe).ConfigureAwait(false);
        return (await ScoreAsync(clusterer, order, cancellationToken).ConfigureAwait(false), watch.Elapsed.TotalSeconds);
    }

    /// <summary>A judge over a chat model, reading the texts of the items it is shown.</summary>
    public static ChatClientJudge Judge(Microsoft.Extensions.AI.IChatClient chat, IEnumerable<LabeledText> texts, Microsoft.Extensions.AI.ChatOptions chatOptions, int parallel, bool zeroTemperature, Action<ChatClientJudgeOptions>? configure = null)
    {
        var store = new InMemoryItemTextStore();
        foreach (var text in texts)
        {
            store.Set(text.Id, text.Text);
        }

        var options = new ChatClientJudgeOptions { ChatOptions = chatOptions, MaxParallelRequests = parallel, UseZeroTemperature = zeroTemperature };
        configure?.Invoke(options);
        return new ChatClientJudge(chat, store, options);
    }

    /// <summary>Maintenance until nothing changes: the clustering a long-running service would settle into.</summary>
    private static async Task SettleMaintenanceAsync(Clusterer clusterer, IClusterJudge? judge, CancellationToken cancellationToken, Action<ClusteringStep>? observe = null)
    {
        for (var round = 0; round < MaxSettleRounds; round++)
        {
            var step = await clusterer.MaintainAsync(cancellationToken).ConfigureAwait(false);
            if (judge is not null)
            {
                step = step.Then(await clusterer.SettleAsync(judge, cancellationToken).ConfigureAwait(false));
            }

            observe?.Invoke(step);

            if (step.Merges.Count == 0 && step.Questions.Count == 0 && step.Splits.Count == 0
                && (await clusterer.Store.GetChangedClustersAsync(0, 1, cancellationToken).ConfigureAwait(false)).Count == 0)
            {
                return;
            }
        }

        throw new InvalidOperationException($"Maintenance was still changing clusters after {MaxSettleRounds} rounds.");
    }

    private static async Task<Scores> ScoreAsync(Clusterer clusterer, IReadOnlyList<LabeledText> texts, CancellationToken cancellationToken)
    {
        var clusters = new List<long>(texts.Count);
        foreach (var text in texts)
        {
            clusters.Add(await clusterer.GetClusterOfAsync(text.Id, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Item {text.Id} was left unplaced."));
        }

        return Metrics.Score([.. texts.Select(t => t.Label)], clusters);
    }

    public static IReadOnlyList<LabeledText> Shuffle(IReadOnlyList<LabeledText> texts, int seed)
    {
        var random = new Random(seed);
        return [.. texts.OrderBy(_ => random.Next())];
    }
}
