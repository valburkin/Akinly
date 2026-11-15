namespace Akinly.Evaluation.Tests;

public sealed class ReportWriterTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("akinly-eval-report").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static Scores Perfect(int clusters) => new(1, 1, 1, 1, 1, 1, 1, 1, 1, clusters, 0);

    [Fact]
    public async Task A_report_is_written_as_json_and_markdown_with_its_warnings()
    {
        var report = new EvaluationReport(
            "1.0.0", "2026-11-14", "banking77", "Banking77, CC BY 4.0; the test split", Dataset.Source("banking77", "test"), 3080, 77,
            "ollama:bge-m3", "given", null, 1, 3, 3, 50, 4,
            [
                new SetupResult("Cosine only", "join 0.90", [Perfect(70), Perfect(80)], 0.5),
                new SetupResult("Akinly, judged", "join 0.90", [Perfect(77)], 60, new JudgeUsage("ollama:model", 0.8, 200, 5, 0.12, 0.01, 3)),
            ],
            ["Something to know."]);

        var markdown = ReportWriter.Markdown(report);
        await ReportWriter.WriteAsync(_folder, "results", report, markdown, default);

        Assert.Contains("| Cosine only | join 0.90 | 1.000 ± 0.000 | 1.000 ± 0.000 |", markdown, StringComparison.Ordinal);
        Assert.Contains("| 0.80 | 205 | 12.0% |", markdown, StringComparison.Ordinal);
        Assert.Contains("Note: Something to know.", markdown, StringComparison.Ordinal);
        Assert.Contains("\"Sha256\"", await File.ReadAllTextAsync(Path.Combine(_folder, "results.json")), StringComparison.Ordinal);
        Assert.Equal(["results.json", "results.md"], Directory.GetFiles(_folder).Select(Path.GetFileName).Order());
    }
}
