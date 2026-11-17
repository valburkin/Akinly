using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Akinly.Evaluation;

/// <summary>
/// The labeled texts to measure on, downloaded on first use from a fixed commit, checked against
/// their SHA-256 and never committed: the intent sets Banking77 (PolyAI, CC BY 4.0), CLINC150
/// (Larson et al., CC BY 3.0) and MASSIVE's English part (Amazon, CC BY 4.0), and the short-text
/// stream sets News-T and Tweets-T (Yin et al., 2018); or a JSON-lines file of your own.
/// </summary>
/// <remarks>
/// News-T and Tweets-T have no split and no license stated: both splits are the whole set, in
/// the stream order the stream-clustering papers use, and they are read for evaluation only.
/// </remarks>
public static class Dataset
{
    private const string Banking77 = "https://raw.githubusercontent.com/PolyAI-LDN/task-specific-datasets/57ec275d8078af65b7731c2a98be812d844a6d6b/banking_data/";
    private const string Clinc150 = "https://raw.githubusercontent.com/clinc/oos-eval/828f8093932c8fe6ca7936c3d2e52903b1c523de/data/";
    private const string Massive = "https://huggingface.co/datasets/mteb/amazon_massive_intent/resolve/940fd47a81eaa7f2cc7b129674d945d618ac38c2/";
    private const string Streams = "https://raw.githubusercontent.com/jackyin12/MStream/ac894075f2bbb93405679e237d2d5d11a325078c/MStream/data/";

    /// <summary>Whether a name is one of the public datasets rather than a file.</summary>
    public static bool IsPublic(string name) => name is "banking77" or "clinc150" or "massive" or "intents" or "news-t" or "tweets-t" or "complaints";

    /// <summary>Whether a public dataset has a training split apart from its test split (News-T and Tweets-T do not).</summary>
    public static bool HasSplit(string name) => name is not ("news-t" or "tweets-t");

    /// <summary>Whether a public dataset comes from one file (the pooled <c>intents</c> comes from three).</summary>
    public static bool HasSingleSource(string name) => IsPublic(name) && name != "intents";

    // The intent sets pooled: each label prefixed with its set's name, so groups never mix.
    private static readonly string[] IntentSets = ["banking77", "clinc150", "massive"];

    /// <summary>The dataset's name in reports and file names: a public dataset's, or a file's name without its folder.</summary>
    public static string Name(string name) => IsPublic(name) ? name : Path.GetFileNameWithoutExtension(name);

    /// <summary>Where a split of a public dataset comes from.</summary>
    public static DatasetSource Source(string name, string split) => (name, split) switch
    {
        ("banking77", "train") => new(Banking77 + "train.csv", "b06e26ac675513959a63135f11b94ea7786ed02da65db93a5650d8838cbc664b", "banking77-train.csv"),
        ("banking77", "test") => new(Banking77 + "test.csv", "d12d6e3bc4c3103966ae786dc435913c0c563dfa328f5a3646d0e62cfeeb474d", "banking77-test.csv"),
        ("clinc150", "train" or "test") => new(Clinc150 + "data_full.json", "36923c3705a59e08fe9c3883d8bc2dd966ef93e22cb78ac41171782a698d56e0", "clinc150-data_full.json"),
        ("massive", "train") => new(Massive + "train/en.json.gz", "65e77f0f2596931671074e3d031a481bc798e5800daa2c886ce7aa26cc16cf22", "massive-train-en.json.gz"),
        ("massive", "test") => new(Massive + "test/en.json.gz", "6dd692a9c188f395956b0534b70486c8ed959fd6d7e50d26d419ff169d213894", "massive-test-en.json.gz"),
        ("news-t", "train" or "test") => new(Streams + "News-T", "562d6a1771a4675caedb405d68371348bd7e54db130b633ef5ebd5896157ffbb", "news-t.jsonl"),
        ("tweets-t", "train" or "test") => new(Streams + "Tweets-T", "e1b806af9992e881ff75b76efe259dff8b3483d9be11f1110a777356075070cf", "tweets-t.jsonl"),
        ("complaints", "train" or "test") => Complaints.Source,
        _ => throw new ArgumentException($"'{name}' has no split '{split}'."),
    };

    public static string License(string name) => name switch
    {
        "banking77" => "Banking77, CC BY 4.0, PolyAI (Casanueva et al., 2020)",
        "clinc150" => "CLINC150, CC BY 3.0, Larson et al. (2019); the 150 in-scope intents (out-of-scope queries are left out)",
        "massive" => "MASSIVE, English intents, CC BY 4.0, Amazon (FitzGerald et al., 2022)",
        "intents" => "Banking77 (CC BY 4.0), CLINC150 (CC BY 3.0) and MASSIVE (CC BY 4.0), pooled, labels kept apart",
        "news-t" => "News-T, Yin et al. (2018); no license stated, downloaded for evaluation only and never redistributed",
        "tweets-t" => "Tweets-T, Yin et al. (2018); no license stated, downloaded for evaluation only and never redistributed",
        "complaints" => Complaints.License,
        _ => "your own data",
    };

    /// <summary>A split (<c>train</c> or <c>test</c>) of a public dataset, downloaded and checked on first use.</summary>
    public static async Task<IReadOnlyList<LabeledText>> LoadAsync(string name, string split, string cacheDirectory, CancellationToken cancellationToken)
    {
        if (name == "intents")
        {
            var pooled = new List<LabeledText>();
            foreach (var set in IntentSets)
            {
                pooled.AddRange((await LoadAsync(set, split, cacheDirectory, cancellationToken).ConfigureAwait(false)).Select(t => t with { Label = $"{set}/{t.Label}" }));
            }

            return pooled;
        }

        if (name == "complaints")
        {
            // Prepared by prepare-complaints; odd complaint ids are the training split, even the test split.
            var path = Complaints.SetPath(cacheDirectory);
            return File.Exists(path)
                ? [.. LoadJsonLines(path).Where(t => (long.Parse(t.Id.AsSpan(4), CultureInfo.InvariantCulture) % 2 == 1) == (split == "train"))]
                : throw new ArgumentException($"{path} does not exist: run prepare-complaints first.");
        }

        var source = Source(name, split);
        var content = await DownloadAsync(source, cacheDirectory, cancellationToken).ConfigureAwait(false);
        var (texts, expected) = name switch
        {
            "banking77" => (ParseBanking77(content, split), split == "train" ? 10_003 : 3_080),
            "clinc150" => (ParseClinc150(content, split), split == "train" ? 15_000 : 4_500),
            "massive" => (ParseJsonLines(content, "m" + split, "id", "text", "label"), split == "train" ? 11_514 : 2_974),
            "news-t" => (ParseJsonLines(content, "n", "Id", "textCleaned", "clusterNo"), 11_109),
            _ => (ParseJsonLines(content, "t", "Id", "textCleaned", "clusterNo"), 30_322),
        };
        return texts.Count == expected
            ? texts
            : throw new InvalidDataException($"{name} {split} has {texts.Count} texts, not {expected}: the parser no longer matches the file.");
    }

    /// <summary>
    /// A sample of <paramref name="count"/> texts in which every label keeps its share, rounded
    /// by largest remainder so the sample has exactly that many; repeatable for a seed. A count of
    /// zero, or at least the number of texts, returns them all.
    /// </summary>
    public static IReadOnlyList<LabeledText> Sample(IReadOnlyList<LabeledText> texts, int count, int seed)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count == 0 || count >= texts.Count)
        {
            return texts;
        }

        var random = new Random(seed);
        var groups = texts.GroupBy(t => t.Label).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (Members: g.OrderBy(_ => random.Next()).ToList(), Quota: (double)g.Count() * count / texts.Count))
            .ToList();
        var taken = groups.Select(g => (int)Math.Floor(g.Quota)).ToArray();
        var left = count - taken.Sum();
        foreach (var i in Enumerable.Range(0, groups.Count).OrderByDescending(i => groups[i].Quota - taken[i]).ThenBy(i => i).Take(left))
        {
            taken[i]++;
        }

        return [.. groups.SelectMany((g, i) => g.Members.Take(taken[i])).OrderBy(_ => random.Next())];
    }

    /// <summary>
    /// A JSON-lines file of <c>{"id", "text", "label"}</c> objects, all three strings, ids unique.
    /// A malformed line fails with its line number.
    /// </summary>
    public static IReadOnlyList<LabeledText> LoadJsonLines(string path)
    {
        var texts = new List<LabeledText>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var number = 0;
        foreach (var line in File.ReadLines(path))
        {
            number++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                string Field(string field) =>
                    document.RootElement.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
                        ? text
                        : throw new InvalidDataException($"needs a non-empty string \"{field}\"");
                var text = new LabeledText(Field("id"), Field("text"), Field("label"));
                texts.Add(ids.Add(text.Id) ? text : throw new InvalidDataException($"repeats the id \"{text.Id}\""));
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException or InvalidOperationException)
            {
                throw new InvalidDataException($"{Path.GetFileName(path)}, line {number}: {exception.Message}", exception);
            }
        }

        return texts.Count > 0 ? texts : throw new InvalidDataException($"{Path.GetFileName(path)} holds no texts.");
    }

    /// <summary>
    /// The file from the cache, or downloaded into it. A download is checked against its hash
    /// before it is moved into place, so an interrupted one never leaves a partial file behind.
    /// </summary>
    private static async Task<string> DownloadAsync(DatasetSource source, string cacheDirectory, CancellationToken cancellationToken)
    {
        var path = await EnsureFileAsync(source, cacheDirectory, cancellationToken).ConfigureAwait(false);
        var cached = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        if (!source.FileName.EndsWith(".gz", StringComparison.Ordinal))
        {
            return Encoding.UTF8.GetString(cached);
        }

        using var compressed = new GZipStream(new MemoryStream(cached), CompressionMode.Decompress);
        using var reader = new StreamReader(compressed, Encoding.UTF8);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The path of the file in the cache, downloaded into it if missing, checked against its hash either way.</summary>
    public static async Task<string> EnsureFileAsync(DatasetSource source, string cacheDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(cacheDirectory);
        var path = Path.Combine(cacheDirectory, source.FileName);
        if (!File.Exists(path))
        {
            using var http = new HttpClient();
            var bytes = await http.GetByteArrayAsync(new Uri(source.Url), cancellationToken).ConfigureAwait(false);
            Verify(bytes, source, source.Url);
            var partial = $"{path}.{Guid.NewGuid():N}.partial";
            await File.WriteAllBytesAsync(partial, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(partial, path, overwrite: true);
        }

        await using (var stream = File.OpenRead(path))
        {
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            if (hash != source.Sha256)
            {
                throw new InvalidDataException($"{path} has SHA-256 {hash}, not the pinned {source.Sha256}. Delete it from the cache to download it again.");
            }
        }

        return path;
    }

    private static void Verify(byte[] bytes, DatasetSource source, string where)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (hash != source.Sha256)
        {
            throw new InvalidDataException($"{where} has SHA-256 {hash}, not the pinned {source.Sha256}. Delete it from the cache to download it again.");
        }
    }

    private static List<LabeledText> ParseBanking77(string csv, string split)
    {
        var texts = new List<LabeledText>();
        foreach (var record in ParseCsv(csv).Skip(1))
        {
            texts.Add(record.Count == 2
                ? new LabeledText(string.Create(CultureInfo.InvariantCulture, $"b{split}{texts.Count + 1}"), record[0].Trim(), record[1].Trim())
                : throw new InvalidDataException($"Banking77 {split}, record {texts.Count + 1}, has {record.Count} fields, not 2."));
        }

        return texts;
    }

    // One JSON object a line; the label may be a string or a number.
    private static List<LabeledText> ParseJsonLines(string content, string prefix, string id, string text, string label)
    {
        var texts = new List<LabeledText>();
        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var value = root.GetProperty(label);
            texts.Add(new LabeledText(
                prefix + root.GetProperty(id).GetString(),
                root.GetProperty(text).GetString()!,
                value.ValueKind == JsonValueKind.Number ? value.GetInt32().ToString(CultureInfo.InvariantCulture) : value.GetString()!));
        }

        return texts;
    }

    private static List<LabeledText> ParseClinc150(string json, string split)
    {
        using var document = JsonDocument.Parse(json);
        var texts = new List<LabeledText>();
        foreach (var pair in document.RootElement.GetProperty(split).EnumerateArray())
        {
            texts.Add(new LabeledText(string.Create(CultureInfo.InvariantCulture, $"c{split}{texts.Count + 1}"), pair[0].GetString()!, pair[1].GetString()!));
        }

        return texts;
    }

    /// <summary>
    /// The records of a CSV text (RFC 4180): commas separate fields, double quotes enclose them,
    /// two double quotes are one, and a quoted field may span lines. Blank lines are skipped.
    /// </summary>
    internal static List<List<string>> ParseCsv(string text)
    {
        var records = new List<List<string>>();
        var record = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var any = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == '"')
            {
                (quoted, any) = (true, true);
            }
            else if (c == ',')
            {
                record.Add(field.ToString());
                (field, any) = (new StringBuilder(), true);
            }
            else if (c is '\r' or '\n')
            {
                if (any || field.Length > 0)
                {
                    record.Add(field.ToString());
                    records.Add(record);
                }

                (record, field, any) = ([], new StringBuilder(), false);
            }
            else
            {
                field.Append(c);
            }
        }

        if (any || field.Length > 0)
        {
            record.Add(field.ToString());
            records.Add(record);
        }

        return records;
    }
}
