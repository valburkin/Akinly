namespace Akinly.Evaluation.Tests;

public sealed class DatasetTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("akinly-eval-tests").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void A_sample_keeps_every_label_in_proportion_and_is_repeatable()
    {
        var texts = Enumerable.Range(0, 300).Select(i => new LabeledText($"t{i}", "x", i % 3 == 0 ? "a" : "b")).ToList();

        var sample = Dataset.Sample(texts, 60, seed: 5);

        Assert.Equal(60, sample.Count);
        Assert.Equal(20, sample.Count(t => t.Label == "a"));
        Assert.Equal(sample.Select(t => t.Id), Dataset.Sample(texts, 60, seed: 5).Select(t => t.Id));
        Assert.Same(texts, Dataset.Sample(texts, 0, seed: 5));
        Assert.Same(texts, Dataset.Sample(texts, 300, seed: 5));
    }

    [Fact]
    public void A_sample_has_exactly_the_count_asked_even_with_many_tiny_labels()
    {
        // 100 labels of one text and 10 of 30: shares round to fractions everywhere.
        var texts = Enumerable.Range(0, 100).Select(i => new LabeledText($"s{i}", "x", $"single{i}"))
            .Concat(Enumerable.Range(0, 300).Select(i => new LabeledText($"g{i}", "x", $"group{i % 10}")))
            .ToList();

        var sample = Dataset.Sample(texts, 101, seed: 2);

        Assert.Equal(101, sample.Count);
        Assert.Equal(sample.Count, sample.Select(t => t.Id).Distinct().Count());
        Assert.InRange(sample.Count(t => t.Label.StartsWith("single", StringComparison.Ordinal)), 20, 26);
        Assert.All(Enumerable.Range(0, 10), g => Assert.InRange(sample.Count(t => t.Label == $"group{g}"), 7, 8));
    }

    [Fact]
    public void Csv_fields_may_be_quoted_with_commas_doubled_quotes_and_line_breaks()
    {
        var records = Dataset.ParseCsv("text,category\r\n\"a, b\",\"say \"\"hi\"\"\"\n\n\"two\nlines\",x\r\nlast,");

        Assert.Equal(4, records.Count);
        Assert.Equal(["a, b", "say \"hi\""], records[1]);
        Assert.Equal(["two\nlines", "x"], records[2]);
        Assert.Equal(["last", ""], records[3]);
    }

    [Fact]
    public void A_json_lines_file_is_read_with_its_ids_and_labels()
    {
        var path = Write("""
            {"id": "1", "text": "Card declined", "label": "card"}

            {"id": "2", "text": "Lost my card", "label": "card"}
            """);

        Assert.Equal([new LabeledText("1", "Card declined", "card"), new LabeledText("2", "Lost my card", "card")], Dataset.LoadJsonLines(path));
        Assert.Equal("data", Dataset.Name(path));
    }

    [Theory]
    [InlineData("{\"id\": \"1\", \"text\": \"a\"}", "line 1", "label")]
    [InlineData("{\"id\": \"1\", \"text\": \"a\", \"label\": \"x\"}\n{\"id\": \"1\", \"text\": \"b\", \"label\": \"x\"}", "line 2", "repeats")]
    [InlineData("{\"id\": 1, \"text\": \"a\", \"label\": \"x\"}", "line 1", "id")]
    [InlineData("not json", "line 1", "")]
    [InlineData("", "no texts", "")]
    public void A_malformed_json_lines_file_fails_with_the_line(string content, string where, string what)
    {
        var exception = Assert.Throws<InvalidDataException>(() => Dataset.LoadJsonLines(Write(content)));

        Assert.Contains(where, exception.Message, StringComparison.Ordinal);
        Assert.Contains(what, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_public_split_is_pinned_to_a_commit_and_a_hash()
    {
        foreach (var (name, split) in new[] { ("banking77", "train"), ("banking77", "test"), ("clinc150", "train"), ("clinc150", "test") })
        {
            var source = Dataset.Source(name, split);
            Assert.DoesNotContain("/master/", source.Url, StringComparison.Ordinal);
            Assert.Matches("^[0-9a-f]{64}$", source.Sha256);
        }

        Assert.Throws<ArgumentException>(() => Dataset.Source("clinc150", "validation"));
        Assert.False(Dataset.IsPublic("banking77.jsonl"));
    }

    private string Write(string content)
    {
        var path = Path.Combine(_folder, "data.jsonl");
        File.WriteAllText(path, content);
        return path;
    }
}
