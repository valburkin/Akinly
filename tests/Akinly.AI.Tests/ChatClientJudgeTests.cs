using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Akinly.AI.Tests;

public sealed class ChatClientJudgeTests
{
    private static readonly InMemoryItemTextStore Texts = Make();

    private static InMemoryItemTextStore Make()
    {
        var texts = new InMemoryItemTextStore();
        texts.Set("heat", "My heating is broken");
        texts.Set("rent", "I can't afford the rent");
        texts.Set("m", "The furnace stopped working");
        return texts;
    }

    private static JoinQuestion Join(long id, string item) => new(new QuestionId(id), item, 1, 0.7, null, ["m"]);

    /// <summary>Says "same" when the new text mentions heating.</summary>
    private static string ByKeyword(IReadOnlyList<ChatMessage> messages, ChatOptions? _) =>
        JsonDocument.Parse(messages[1].Text).RootElement.GetProperty("new_text").GetString()!.Contains("heat", StringComparison.OrdinalIgnoreCase)
            ? """{"same": true}"""
            : """{"same": false}""";

    [Fact]
    public async Task Answers_map_to_verdicts_in_question_order()
    {
        var judge = new ChatClientJudge(new FakeChatClient(ByKeyword), Texts);

        var verdicts = await judge.JudgeAsync([Join(1, "heat"), Join(2, "rent"), Join(3, "gone")], default);

        Assert.Equal(
            [Verdict.Same(new QuestionId(1)), Verdict.Different(new QuestionId(2)), Verdict.Unanswerable(new QuestionId(3))],
            verdicts);
    }

    [Fact]
    public async Task An_unreadable_answer_or_a_failing_request_is_a_failed_verdict()
    {
        var unreadable = new ChatClientJudge(new FakeChatClient((_, _) => "I'm not sure."), Texts);
        var failing = new ChatClientJudge(new FakeChatClient((_, _) => "") { Throw = new HttpRequestException("503") }, Texts);

        Assert.Equal(VerdictKind.Failed, (await unreadable.JudgeAsync([Join(1, "heat")], default))[0].Kind);
        Assert.Equal(VerdictKind.Failed, (await failing.JudgeAsync([Join(1, "heat")], default))[0].Kind);
    }

    [Fact]
    public async Task Cancellation_is_not_mistaken_for_a_failed_answer()
    {
        var judge = new ChatClientJudge(new FakeChatClient(ByKeyword) { Delay = TimeSpan.FromSeconds(10) }, Texts);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => judge.JudgeAsync([Join(1, "heat")], cancellation.Token));
    }

    [Fact]
    public async Task Requests_switch_tools_off_ask_for_the_schema_and_keep_the_callers_model()
    {
        var chat = new FakeChatClient(ByKeyword);
        var configured = new ChatOptions { ModelId = "my-model", Tools = [AIFunctionFactory.Create(() => "secret", "leak")], ToolMode = ChatToolMode.Auto };
        var judge = new ChatClientJudge(chat, Texts, new ChatClientJudgeOptions { ChatOptions = configured });

        await judge.JudgeAsync([Join(1, "heat")], default);

        var options = Assert.Single(chat.Requests).Options!;
        Assert.Equal("my-model", options.ModelId);
        Assert.Null(options.Tools);
        Assert.Equal(ChatToolMode.None, options.ToolMode);
        Assert.Null(options.Temperature);   // current Claude and OpenAI reasoning models reject one
        Assert.IsType<ChatResponseFormatJson>(options.ResponseFormat);
        Assert.NotNull(configured.Tools);
    }

    [Fact]
    public async Task Structured_output_can_be_switched_off_and_a_set_temperature_is_kept()
    {
        var chat = new FakeChatClient(ByKeyword);
        var judge = new ChatClientJudge(chat, Texts, new ChatClientJudgeOptions { UseStructuredOutput = false, ChatOptions = new ChatOptions { Temperature = 0.3f } });

        await judge.JudgeAsync([Join(1, "heat")], default);

        var options = Assert.Single(chat.Requests).Options!;
        Assert.Null(options.ResponseFormat);
        Assert.Equal(0.3f, options.Temperature);
    }

    [Fact]
    public async Task At_most_the_configured_number_of_requests_run_at_once()
    {
        var chat = new FakeChatClient(ByKeyword) { Delay = TimeSpan.FromMilliseconds(30) };
        var judge = new ChatClientJudge(chat, Texts, new ChatClientJudgeOptions { MaxParallelRequests = 2 });

        var verdicts = await judge.JudgeAsync([.. Enumerable.Range(1, 8).Select(i => Join(i, "heat"))], default);

        Assert.Equal(8, verdicts.Count);
        Assert.InRange(chat.MaxInFlight, 1, 2);
    }

    [Fact]
    public void Invalid_options_are_refused()
    {
        var options = new ChatClientJudgeOptions { Criterion = " ", MaxParallelRequests = 0, MaxTextLength = 0 };

        var exception = Assert.Throws<ArgumentException>(() => new ChatClientJudge(new FakeChatClient(ByKeyword), Texts, options));

        Assert.Contains("Criterion", exception.Message, StringComparison.Ordinal);
        Assert.Contains("MaxParallelRequests", exception.Message, StringComparison.Ordinal);
        Assert.Contains("MaxTextLength", exception.Message, StringComparison.Ordinal);
    }
}
