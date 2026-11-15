using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Akinly.AI.Tests;

public sealed class ChoiceJudgeTests
{
    private static readonly InMemoryItemTextStore Texts = Make();

    private static InMemoryItemTextStore Make()
    {
        var texts = new InMemoryItemTextStore();
        texts.Set("item", "How many points are on my store card?");
        texts.Set("a", "What is my account balance?");
        texts.Set("b", "How many reward points do I have?");
        texts.Set("c", "Freeze my card");
        return texts;
    }

    private static JoinQuestion Choice(long id) =>
        new(new QuestionId(id), "item", 10, 0.8, null, ["a"]) { OtherChoices = [new JoinChoice(20, 0.75, null, ["b"]), new JoinChoice(30, 0.7, null, ["c"])] };

    [Theory]
    [InlineData("""{"group": 1}""", 10L)]
    [InlineData("""{"group": 2}""", 20L)]
    [InlineData("```json\n{\"group\": 3}\n```", 30L)]
    public async Task A_picked_group_is_a_verdict_for_that_cluster(string answer, long cluster)
    {
        var judge = new ChatClientJudge(new FakeChatClient((_, _) => answer), Texts);

        var verdict = Assert.Single(await judge.JudgeAsync([Choice(1)], default));

        Assert.Equal(VerdictKind.Same, verdict.Kind);
        Assert.Equal(cluster == 10 ? null : cluster, verdict.PickedClusterId);
    }

    [Theory]
    [InlineData("""{"group": 0}""", VerdictKind.Different)]
    [InlineData("""{"group": 4}""", VerdictKind.Failed)]
    [InlineData("""{"group": -1}""", VerdictKind.Failed)]
    [InlineData("""{"same": true}""", VerdictKind.Failed)]
    public async Task None_is_different_and_a_group_not_offered_is_failed(string answer, VerdictKind kind)
    {
        var judge = new ChatClientJudge(new FakeChatClient((_, _) => answer), Texts);

        Assert.Equal(kind, Assert.Single(await judge.JudgeAsync([Choice(1)], default)).Kind);
    }

    [Fact]
    public async Task The_prompt_numbers_the_groups_and_asks_for_a_group()
    {
        var chat = new FakeChatClient((_, _) => """{"group": 0}""");
        var judge = new ChatClientJudge(chat, Texts);

        await judge.JudgeAsync([Choice(1)], default);

        var (messages, options) = Assert.Single(chat.Requests);
        var data = JsonDocument.Parse(messages[1].Text).RootElement;
        Assert.Equal(3, data.GetProperty("groups").GetArrayLength());
        Assert.Equal("Freeze my card", data.GetProperty("groups")[2].GetProperty("members")[0].GetString());
        Assert.Contains("\"group\"", messages[0].Text, StringComparison.Ordinal);
        Assert.Contains("group", Assert.IsType<ChatResponseFormatJson>(options!.ResponseFormat).Schema!.Value.GetRawText(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ClusteringPreference.Balanced, null)]
    [InlineData(ClusteringPreference.Precision, "pick none")]
    [InlineData(ClusteringPreference.Recall, "rather than none")]
    public async Task The_preference_adds_its_tie_break_to_choice_questions(ClusteringPreference preference, string? expected)
    {
        var chat = new FakeChatClient((_, _) => """{"group": 0}""");
        var judge = new ChatClientJudge(chat, Texts, new ChatClientJudgeOptions { Preference = preference });

        await judge.JudgeAsync([Choice(1)], default);

        var system = Assert.Single(chat.Requests).Messages[0].Text;
        Assert.Equal(expected is not null, system.Contains("unsure", StringComparison.Ordinal));
        if (expected is not null)
        {
            Assert.Contains(expected, system, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(ClusteringPreference.Precision, "do not belong together")]
    [InlineData(ClusteringPreference.Recall, "answer that they belong together")]
    public async Task The_preference_adds_its_tie_break_to_yes_or_no_questions(ClusteringPreference preference, string expected)
    {
        var chat = new FakeChatClient((_, _) => """{"same": false}""");
        var judge = new ChatClientJudge(chat, Texts, new ChatClientJudgeOptions { Preference = preference, JoinInstructions = "Custom." });

        await judge.JudgeAsync([new JoinQuestion(new QuestionId(1), "item", 10, 0.8, null, ["a"])], default);

        var system = Assert.Single(chat.Requests).Messages[0].Text;
        Assert.StartsWith("Custom.", system, StringComparison.Ordinal);
        Assert.Contains(expected, system, StringComparison.Ordinal);
    }

    [Fact]
    public void An_undefined_preference_is_invalid() =>
        Assert.Contains(new ChatClientJudgeOptions { Preference = (ClusteringPreference)7 }.Validate(), e => e.Contains("Preference", StringComparison.Ordinal));

    [Fact]
    public async Task A_choice_whose_cluster_has_nothing_to_show_cannot_be_put()
    {
        var question = new JoinQuestion(new QuestionId(1), "item", 10, 0.8, null, ["a"]) { OtherChoices = [new JoinChoice(20, 0.75, null, ["gone"])] };
        var judge = new ChatClientJudge(new FakeChatClient((_, _) => """{"group": 1}"""), Texts);

        Assert.Equal(VerdictKind.Unanswerable, Assert.Single(await judge.JudgeAsync([question], default)).Kind);
    }

    [Theory]
    [InlineData("<think>group 2 looks close, but {\"group\": 9} is not offered</think>{\"group\": 3}", VerdictKind.Same, 30L)]
    [InlineData("I would say {\"group\": 2}.", VerdictKind.Same, 20L)]
    [InlineData("{\"group\": 0}", VerdictKind.Different, null)]
    [InlineData("two", VerdictKind.Failed, null)]
    public void TryReadVerdict_maps_groups_to_clusters(string answer, VerdictKind kind, long? cluster)
    {
        var read = JudgePrompts.TryReadVerdict(Choice(7), answer, out var verdict);

        Assert.Equal(kind != VerdictKind.Failed, read);
        Assert.Equal((new QuestionId(7), kind, cluster), (verdict.QuestionId, verdict.Kind, verdict.PickedClusterId));
    }

    [Fact]
    public void TryReadVerdict_reads_yes_or_no_for_a_question_without_choices()
    {
        var question = new JoinQuestion(new QuestionId(3), "item", 10, 0.8, null, ["a"]);

        Assert.True(JudgePrompts.TryReadVerdict(question, "yes", out var verdict));
        Assert.Equal(Verdict.Same(new QuestionId(3)), verdict);
        Assert.False(JudgePrompts.TryReadVerdict(question, """{"group": 1}""", out _));
    }

    [Fact]
    public async Task Labels_stay_out_of_a_choice_prompt_when_turned_off()
    {
        var question = new JoinQuestion(new QuestionId(1), "item", 10, 0.8, "Balance", ["a"]) { OtherChoices = [new JoinChoice(20, 0.75, "Points", ["b"])] };
        var chat = new FakeChatClient((_, _) => """{"group": 0}""");

        await new ChatClientJudge(chat, Texts, new ChatClientJudgeOptions { IncludeLabels = false }).JudgeAsync([question], default);

        var groups = JsonDocument.Parse(Assert.Single(chat.Requests).Messages[1].Text).RootElement.GetProperty("groups");
        Assert.All(groups.EnumerateArray(), g => Assert.Equal(JsonValueKind.Null, g.GetProperty("label").ValueKind));
    }

    [Fact]
    public async Task Custom_choice_instructions_replace_the_default_and_keep_the_criterion()
    {
        var chat = new FakeChatClient((_, _) => """{"group": 1}""");
        var options = new ChatClientJudgeOptions { ChoiceInstructions = "Pick one about {criterion}.", Criterion = "the same bug" };

        await new ChatClientJudge(chat, Texts, options).JudgeAsync([Choice(1)], default);

        Assert.Equal("Pick one about the same bug.", Assert.Single(chat.Requests).Messages[0].Text);
    }

    [Fact]
    public void Without_structured_output_a_choice_request_asks_for_no_schema() =>
        Assert.Null(JudgePrompts.CreateRequestOptions(new ChatClientJudgeOptions { UseStructuredOutput = false }, choice: true).ResponseFormat);

    [Fact]
    public async Task A_choice_question_whose_item_text_is_gone_cannot_be_put()
    {
        var question = new JoinQuestion(new QuestionId(1), "gone", 10, 0.8, null, ["a"]) { OtherChoices = [new JoinChoice(20, 0.75, null, ["b"])] };
        var chat = new FakeChatClient((_, _) => """{"group": 1}""");

        var verdict = Assert.Single(await new ChatClientJudge(chat, Texts).JudgeAsync([question], default));

        Assert.Equal(VerdictKind.Unanswerable, verdict.Kind);
        Assert.Empty(chat.Requests);
    }
}
