namespace Akinly.Evaluation.Tests;

public sealed class ArgumentsTests
{
    [Fact]
    public void Options_are_read_by_name_with_typed_values()
    {
        var arguments = Arguments.Parse(["evaluate", "--sample", "500", "--join", "0.9", "--dataset", "clinc150"]);

        arguments.Allow("sample", "join", "dataset");
        Assert.Equal("evaluate", arguments.Command);
        Assert.Equal(500, arguments.Number("sample", 0));
        Assert.Equal(3, arguments.Number("orders", 3));
        Assert.Equal(0.9, arguments.Similarity("join"));
        Assert.Equal("clinc150", arguments.Get("dataset", "banking77"));
    }

    [Theory]
    [InlineData("--sample", "--sample needs a value")]
    [InlineData("--sample --orders 3", "--sample needs a value")]
    [InlineData("--sample 1 --sample 2", "given twice")]
    [InlineData("sample 1", "Expected an option")]
    [InlineData("--sample -1", "at least 0")]
    [InlineData("--sample many", "whole number")]
    [InlineData("--join 1.5", "above 0 and at most 1")]
    [InlineData("--judge-orders 3", "Unknown option --judge-orders")]
    public void Mistakes_are_refused_with_a_message(string options, string message)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
        {
            var arguments = Arguments.Parse(["evaluate", .. options.Split(' ')]);
            arguments.Allow("sample", "orders", "join");
            arguments.Number("sample", 0);
            arguments.Similarity("join");
        });

        Assert.Contains(message, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_commands_and_partial_thresholds_are_refused_before_any_download()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Cli.RunAsync(["bogus"], default));
        var partial = await Assert.ThrowsAsync<ArgumentException>(() => Cli.RunAsync(["evaluate", "--join", "0.9"], default));
        Assert.Contains("all four", partial.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentException>(() => Cli.RunAsync(["evaluate", "--dataset", "missing.jsonl"], default));
        await Assert.ThrowsAsync<ArgumentException>(() => Cli.RunAsync(["calibrate", "--embedder", "bge-m3"], default));
        Assert.Equal(0, await Cli.RunAsync([], default));
    }
}
