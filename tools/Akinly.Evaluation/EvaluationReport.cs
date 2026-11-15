namespace Akinly.Evaluation;

/// <summary>Everything needed to read, and to repeat, an evaluation.</summary>
/// <param name="Akinly">The Akinly version and commit measured.</param>
/// <param name="Date">When, as yyyy-MM-dd (UTC).</param>
/// <param name="Dataset">The dataset's name.</param>
/// <param name="Data">Its license and the part used.</param>
/// <param name="Source">Where the evaluated split came from, with its SHA-256; null for your own file.</param>
/// <param name="Texts">How many texts were clustered.</param>
/// <param name="Labels">How many groups they belong to.</param>
/// <param name="Embedder">The embedding model, as provider:model.</param>
/// <param name="Thresholds">Where the thresholds came from.</param>
/// <param name="Calibration">The calibration behind them, if they were calibrated.</param>
/// <param name="Seed">The seed for samples and orders; order <c>o</c> (from 0) shuffles with <c>Seed + 100 + o</c>.</param>
/// <param name="Orders">Input orders per cosine-only setup.</param>
/// <param name="JudgedOrders">Input orders for the judged setup.</param>
/// <param name="Batch">Items per call to the clusterer.</param>
/// <param name="Parallel">Questions put to the judge at once.</param>
/// <param name="Results">Each setup's results.</param>
/// <param name="Warnings">What a reader should know about these numbers.</param>
public sealed record EvaluationReport(
    string Akinly, string Date, string Dataset, string Data, DatasetSource? Source, int Texts, int Labels, string Embedder,
    string Thresholds, Calibration? Calibration, int Seed, int Orders, int JudgedOrders, int Batch, int Parallel,
    IReadOnlyList<SetupResult> Results, IReadOnlyList<string> Warnings);
