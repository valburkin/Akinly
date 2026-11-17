using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Parquet;
using Parquet.Schema;

namespace Akinly.Evaluation;

/// <summary>
/// The realistic set: one-sentence need statements, all on one template ("People struggle to
/// ..."), written by a local model from public complaint narratives of the CFPB's Consumer
/// Complaint Database (a US government work; the Hugging Face copy's card lists CC0-1.0). Unlike
/// the intent sets, most statements have no close partner, and the shared template raises every
/// similarity, as in apps that cluster statements extracted from longer texts.
/// </summary>
/// <remarks>
/// The narratives come from one file of a Hugging Face copy of the database, pinned to a commit
/// and checked against its SHA-256. A deterministic sample, capped per issue so no one issue
/// dominates, is rewritten by the writer model; each statement is cached, so an interrupted run
/// resumes. The set is written to the cache folder and never committed. Labels are the
/// database's product and issue: too coarse to score clusters by, but a cluster that mixes
/// products has misplaced items.
/// </remarks>
public static class Complaints
{
    /// <summary>The narratives: the first of three files of the copy with narratives, about 300 MB.</summary>
    public static readonly DatasetSource Source = new(
        "https://huggingface.co/datasets/BEE-spoke-data/consumer-finance-complaints/resolve/088cc7308d4afc2a880f2329d08e2f7a09188ec6/has-text/train-00000-of-00003.parquet",
        "60902c32f0a6b3db25a378570d421765d0a15ced396c0c471a9d60e49b38d833",
        "cfpb-has-text-00000.parquet");

    public const string License = "CFPB Consumer Complaint Database (a US government work; the Hugging Face copy lists CC0-1.0), narratives rewritten as one-sentence need statements by a local model";

    /// <summary>The prepared set's file in the cache folder.</summary>
    public static string SetPath(string cacheDirectory) => Path.Combine(cacheDirectory, "complaints.jsonl");

    public const string Instructions = """
        You turn a consumer complaint into one need statement.
        Write one sentence that starts with "People struggle to" and names the specific problem the consumer faces, worded so that people with the same problem would get the same sentence.
        Leave out names, companies, amounts, dates, places and account details. At most 25 words.
        Answer with the sentence only.
        """;

    private const int MinNarrative = 300;
    private const int MaxNarrative = 4000;

    /// <summary>A complaint with a narrative.</summary>
    public sealed record Complaint(long Id, string Product, string Issue, string Narrative);

    /// <summary>Writes the realistic set: <c>prepare-complaints [--writer ollama:qwen3.5:9b] [--count 8000] [--parallel 4]</c>.</summary>
    public static async Task PrepareAsync(Arguments arguments, CancellationToken cancellationToken)
    {
        var cache = arguments.Get("cache", ".eval-cache");
        var writer = arguments.Get("writer", "ollama:qwen3.5:9b");
        Models.Check(writer);
        var count = arguments.Number("count", 8000, minimum: 100);
        var parallel = arguments.Number("parallel", 4, minimum: 1);
        var seed = arguments.Number("seed", 1);

        var path = await Dataset.EnsureFileAsync(Source, cache, cancellationToken).ConfigureAwait(false);
        var complaints = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
        var sample = Sample(complaints, count + count / 10, perIssue: Math.Max(10, count / 50), seed);
        Console.Error.WriteLine($"{complaints.Count:N0} narratives, {sample.Count:N0} sampled from {sample.Select(c => (c.Product, c.Issue)).Distinct().Count()} issues");

        var folder = Path.Combine(cache, "statements", Embeddings.Safe(writer) + "-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Instructions)))[..8]);
        Directory.CreateDirectory(folder);
        using var chat = Models.Chat(writer);
        var options = Models.ChatOptions(writer);
        options.Temperature = Models.AcceptsTemperature(writer) ? 0 : null;
        options.MaxOutputTokens = 120;

        var written = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Parallel.ForEachAsync(sample, new ParallelOptions { MaxDegreeOfParallelism = parallel, CancellationToken = cancellationToken }, async (complaint, ct) =>
        {
            var file = Path.Combine(folder, complaint.Id.ToString(CultureInfo.InvariantCulture) + ".txt");
            if (!File.Exists(file))
            {
                var response = await chat.GetResponseAsync(
                    [new ChatMessage(ChatRole.System, Instructions), new ChatMessage(ChatRole.User, complaint.Narrative)], options, ct).ConfigureAwait(false);
                var partial = $"{file}.{Guid.NewGuid():N}.partial";
                await File.WriteAllTextAsync(partial, response.Text.Trim(), ct).ConfigureAwait(false);
                File.Move(partial, file, overwrite: true);
            }

            var done = Interlocked.Increment(ref written);
            if (done % 20 == 0)
            {
                Console.Error.Write($"\r{done}/{sample.Count} statements, {watch.Elapsed:hh\\:mm\\:ss}");
            }
        }).ConfigureAwait(false);
        Console.Error.WriteLine();

        // The set: the sample's valid statements in sample order, up to the count.
        var lines = new List<string>();
        var rejected = 0;
        foreach (var complaint in sample)
        {
            var statement = Clean(await File.ReadAllTextAsync(Path.Combine(folder, complaint.Id.ToString(CultureInfo.InvariantCulture) + ".txt"), cancellationToken).ConfigureAwait(false));
            if (statement is null)
            {
                rejected++;
                continue;
            }

            if (lines.Count < count)
            {
                lines.Add(JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["id"] = "cfpb" + complaint.Id.ToString(CultureInfo.InvariantCulture),
                    ["text"] = statement,
                    ["label"] = $"{complaint.Product} | {complaint.Issue}",
                }));
            }
        }

        if (lines.Count < count)
        {
            throw new InvalidDataException($"Only {lines.Count} of {count} statements were usable ({rejected} rejected): ask for fewer, or use another writer.");
        }

        var output = SetPath(cache);
        await File.WriteAllLinesAsync(output, lines, cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"{lines.Count} statements written to {output} ({rejected} rejected), writer {writer}");
    }

    /// <summary>A written statement, or null when it is not one sentence on the template.</summary>
    internal static string? Clean(string text)
    {
        var line = text.Trim().Trim('"').Trim();
        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        return line.StartsWith("People struggle to ", StringComparison.Ordinal) && !line.Contains('\n', StringComparison.Ordinal) && words is >= 6 and <= 40
            ? line
            : null;
    }

    /// <summary>
    /// A sample in a random order, repeatable for a seed: at most <paramref name="perIssue"/>
    /// complaints of each product and issue, so the commonest issues do not crowd out the rest.
    /// </summary>
    internal static IReadOnlyList<Complaint> Sample(IReadOnlyList<Complaint> complaints, int count, int perIssue, int seed)
    {
        var random = new Random(seed);
        var shuffled = complaints.OrderBy(c => c.Id).OrderBy(_ => random.Next()).ToList();
        var taken = new Dictionary<(string, string), int>();
        var sample = new List<Complaint>(count);
        foreach (var complaint in shuffled)
        {
            var key = (complaint.Product, complaint.Issue);
            if (taken.GetValueOrDefault(key) < perIssue)
            {
                taken[key] = taken.GetValueOrDefault(key) + 1;
                sample.Add(complaint);
                if (sample.Count == count)
                {
                    break;
                }
            }
        }

        return sample;
    }

    /// <summary>The complaints with a narrative of a usable length, mostly unredacted.</summary>
    private static async Task<List<Complaint>> ReadAsync(string path, CancellationToken cancellationToken)
    {
        await using var reader = await ParquetReader.CreateAsync(path, cancellationToken: cancellationToken).ConfigureAwait(false);
        var fields = reader.Schema.GetDataFields();
        DataField Field(string name) => fields.FirstOrDefault(f => f.Name == name) ?? throw new InvalidDataException($"The narratives file has no column '{name}'.");
        var (idField, productField, issueField, narrativeField) = (Field("Complaint ID"), Field("Product"), Field("Issue"), Field("Consumer complaint narrative"));
        var complaints = new List<Complaint>();
        for (var g = 0; g < reader.RowGroupCount; g++)
        {
            using var group = reader.OpenRowGroupReader(g);
            var rows = checked((int)group.RowCount);
            var ids = new long?[rows];
            var products = new string?[rows];
            var issues = new string?[rows];
            var narratives = new string?[rows];
            await group.ReadAsync(idField, ids.AsMemory(), null, cancellationToken).ConfigureAwait(false);
            await group.ReadAsync(productField, products.AsMemory(), null, cancellationToken).ConfigureAwait(false);
            await group.ReadAsync(issueField, issues.AsMemory(), null, cancellationToken).ConfigureAwait(false);
            await group.ReadAsync(narrativeField, narratives.AsMemory(), null, cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < rows; i++)
            {
                if (ids[i] is { } id && products[i] is { Length: > 0 } product && issues[i] is { Length: > 0 } issue
                    && narratives[i] is { Length: >= MinNarrative and <= MaxNarrative } narrative && Redacted(narrative) < 0.2)
                {
                    complaints.Add(new Complaint(id, product, issue, narrative));
                }
            }
        }

        return complaints;
    }

    // The share of a narrative's words that are redaction marks (XXXX).
    private static double Redacted(string narrative)
    {
        var words = narrative.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 0 ? 1 : (double)words.Count(w => w.StartsWith("XX", StringComparison.Ordinal)) / words.Length;
    }
}
