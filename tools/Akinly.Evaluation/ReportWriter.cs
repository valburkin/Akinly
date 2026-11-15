using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Akinly.Evaluation;

/// <summary>Writes reports as JSON and Markdown, each through a temporary file, so a stopped run never leaves half a report.</summary>
public static class ReportWriter
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static async Task WriteAsync(string folder, string name, object report, string markdown, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(folder);
        await WriteFileAsync(Path.Combine(folder, name + ".json"), JsonSerializer.Serialize(report, Json), cancellationToken).ConfigureAwait(false);
        await WriteFileAsync(Path.Combine(folder, name + ".md"), markdown, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteFileAsync(string path, string content, CancellationToken cancellationToken)
    {
        var partial = $"{path}.{Guid.NewGuid():N}.partial";
        await File.WriteAllTextAsync(partial, content, cancellationToken).ConfigureAwait(false);
        File.Move(partial, path, overwrite: true);
    }

    public static string Markdown(EvaluationReport report)
    {
        var culture = CultureInfo.InvariantCulture;
        var text = new StringBuilder();
        text.AppendLine(culture, $"## {report.Dataset}: {report.Texts:N0} texts in {report.Labels} groups, embedded with {report.Embedder}");
        text.AppendLine();
        text.AppendLine(culture, $"Akinly {report.Akinly}, measured {report.Date}. Data: {report.Data}. Thresholds: {report.Thresholds}.");
        text.AppendLine(culture, $"Mean over input orders (± standard deviation): {report.Orders} per cosine-only setup, {report.JudgedOrders} judged; {report.Batch} items per call; seed {report.Seed}.");
        text.AppendLine();
        text.AppendLine("| Setup | Settings | ARI | Accuracy | NMI | Pair precision | Pair recall | Pair F1 | Clusters | Judge answers per item | Tokens per item | Failed answers |");
        text.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var r in report.Results)
        {
            string M(Func<Scores, double> f) => r.Runs.Count > 1 ? $"{r.Mean(f):0.000} ± {r.Spread(f):0.000}" : $"{r.Mean(f):0.000}";
            var judge = r.Judge;
            var questions = judge is null ? "–" : $"{judge.QuestionsPerItem:0.00}";
            var tokens = judge is null ? "–" : $"{judge.InputTokensPerItem + judge.OutputTokensPerItem:N0}";
            var failed = judge is null ? "–" : $"{judge.FailedShare * 100:0.0}%";
            text.AppendLine(culture, $"| {r.Setup} | {r.Settings} | {M(s => s.AdjustedRand)} | {M(s => s.Accuracy)} | {M(s => s.Nmi)} | {M(s => s.PairPrecision)} | {M(s => s.PairRecall)} | {M(s => s.PairF1)} | {r.Runs.Average(s => s.Clusters):N0} | {questions} | {tokens} | {failed} |");
        }

        text.AppendLine();
        text.AppendLine("ARI is the adjusted Rand index (1 is perfect, 0 is chance). Accuracy matches each group to at most one cluster (the Hungarian algorithm) and counts the items in their group's cluster, as intent-discovery papers report it; it is not classification accuracy. NMI uses the arithmetic mean, which makes it equal to V-measure. Pair precision is the share of pairs put together that belong together; pair recall, the share of pairs that belong together that were put together. Judge answers count every answer, retries of failed ones included; a question that failed every attempt was decided by similarity.");
        foreach (var warning in report.Warnings)
        {
            text.AppendLine();
            text.AppendLine(culture, $"Note: {warning}");
        }

        return text.ToString();
    }
}
