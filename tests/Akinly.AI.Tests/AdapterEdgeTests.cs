using System.Text.Json;

namespace Akinly.AI.Tests;

/// <summary>Paths the main tests leave out: resuming, options checks, and prompt corners.</summary>
public sealed class AdapterEdgeTests
{
    [Fact]
    public async Task Settling_resumes_where_an_interrupted_add_left_off()
    {
        var chat = new FakeChatClient((_, _) => """{"same": true}""");
        var options = new ClusteringOptions { JoinSimilarity = 1, CheckSimilarity = 0.1, MergeSimilarity = 1, MergeCheckSimilarity = 1 };
        var clusterer = new TextClusterer(new FakeEmbedder("heat"), chat, options, new TextClustererOptions { LabelClusters = false });
        await clusterer.AddAsync([new TextItem("a", "heat one")]);
        await clusterer.Clusterer.AddAsync([new ClusterItem("b", new float[] { 1, 0.3f })]);   // added around the text clusterer: its question is open
        await clusterer.Texts.SetTextAsync("b", "heat again", default);

        var step = await clusterer.SettleAsync();

        Assert.Contains(new Placement("b", 1, PlacementReason.Judge), step.Placements);
        Assert.Empty(await clusterer.Clusterer.GetOpenQuestionsAsync());
    }

    [Fact]
    public async Task A_refused_add_puts_back_a_text_that_was_already_kept()
    {
        var clusterer = new TextClusterer(new FakeEmbedder("heat"), null, ClusteringOptions.CosineOnly("m", 0.9));
        await clusterer.AddAsync([new TextItem("a", "heat")]);
        await clusterer.Texts.SetTextAsync("b", "an earlier text for b", default);

        await Assert.ThrowsAsync<ArgumentException>(() => clusterer.AddAsync([new TextItem("b", "heat again"), new TextItem("a", "heat twice")]));

        Assert.Equal("an earlier text for b", await clusterer.Texts.GetTextAsync("b", default));
        Assert.Equal("heat", await clusterer.Texts.GetTextAsync("a", default));
    }

    [Fact]
    public async Task Removing_through_the_text_clusterer_forgets_the_texts()
    {
        var clusterer = new TextClusterer(new FakeEmbedder("heat"), null, ClusteringOptions.CosineOnly("m", 0.9));
        await clusterer.AddAsync([new TextItem("a", "heat"), new TextItem("b", "heat 2")]);

        await clusterer.RemoveAsync(["a"]);

        Assert.Null(await clusterer.Texts.GetTextAsync("a", default));
        Assert.Equal(["b"], await clusterer.GetMembersAsync(1));
    }

    [Fact]
    public void Options_that_cannot_work_are_refused()
    {
        var embedder = new FakeEmbedder("heat");

        Assert.Throws<ArgumentException>(() => new TextClusterer(embedder, null, ClusteringOptions.CosineOnly("m", 0.9), new TextClustererOptions { Judge = null! }));
        Assert.Contains("RequestTimeout", new ChatClientJudgeOptions { RequestTimeout = TimeSpan.Zero }.Validate().Single(), StringComparison.Ordinal);
        Assert.Contains("RequestTimeout", new ChatClientLabelerOptions { RequestTimeout = TimeSpan.FromSeconds(-1) }.Validate().Single(), StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => new TextClusterer(embedder, null, new ClusteringOptions { EmbeddingModel = "m", FailedAnswers = null! }));
    }

    [Fact]
    public async Task Merge_prompts_leave_labels_out_when_asked_and_give_up_without_members()
    {
        var texts = new InMemoryItemTextStore();
        texts.Set("a", "first");
        texts.Set("b", "second");
        var merge = new MergeQuestion(new QuestionId(1), 1, 2, 0.8, "Label one", "Label two", ["a"], ["b"]);

        var messages = await JudgePrompts.BuildAsync(merge, texts, new ChatClientJudgeOptions { IncludeLabels = false });
        var unlabeled = await JudgePrompts.BuildAsync(merge with { ExampleItemIds = [] }, texts, new ChatClientJudgeOptions { IncludeLabels = false });

        var data = JsonDocument.Parse(messages![1].Text).RootElement;
        Assert.Equal(JsonValueKind.Null, data.GetProperty("first_group").GetProperty("label").ValueKind);
        Assert.Null(unlabeled);
    }

    [Theory]
    [InlineData("""{"note": "an escaped \" quote { here", "same": false}""", false)]
    [InlineData("""prose } stray brace {"same": true}""", true)]
    [InlineData("""["same", true]""", null)]
    public void Odd_answers_are_read_or_refused_cleanly(string text, bool? expected)
    {
        var read = JudgePrompts.TryParse(text, out var same);

        Assert.Equal(expected is not null, read);
        if (expected is { } value)
        {
            Assert.Equal(value, same);
        }
    }

    [Fact]
    public async Task A_label_answer_in_prose_is_used_as_is_and_an_unparsable_object_is_refused()
    {
        Assert.Equal("Heating", await new ChatClientLabeler(new FakeChatClient((_, _) => "Heating"), new InMemoryItemTextStore()).WriteLabelAsync(["a"]));
        Assert.Null(await new ChatClientLabeler(new FakeChatClient((_, _) => "{not json"), new InMemoryItemTextStore()).WriteLabelAsync(["a"]));
    }

    [Fact]
    public async Task Structured_output_and_zero_temperature_can_be_turned_off_for_labels()
    {
        var chat = new FakeChatClient((_, _) => """{"label": "Heating"}""");
        var labeler = new ChatClientLabeler(chat, new InMemoryItemTextStore(), new ChatClientLabelerOptions { UseStructuredOutput = false, UseZeroTemperature = false, RequestTimeout = null });

        await labeler.WriteLabelAsync(["a"]);

        var options = Assert.Single(chat.Requests).Options!;
        Assert.Null(options.ResponseFormat);
        Assert.Null(options.Temperature);
    }
}
