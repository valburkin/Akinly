using Akinly.Storage;

namespace Akinly.AI.Tests;

public sealed class ChatClientLabelerTests
{
    private static async Task<(Clusterer Clusterer, InMemoryItemTextStore Texts)> ClusterAsync(int size)
    {
        var clusterer = new Clusterer(new InMemoryClusterStore(), ClusteringOptions.CosineOnly("m", 0.5));
        var texts = new InMemoryItemTextStore();
        for (var i = 0; i < size; i++)
        {
            texts.Set($"t{i}", $"The heating is broken, report {i}");
        }

        await clusterer.AddAsync([.. Enumerable.Range(0, size).Select(i => new ClusterItem($"t{i}", new float[] { 1, i * 0.01f }))]);
        return (clusterer, texts);
    }

    [Fact]
    public async Task A_cluster_big_enough_gets_a_label_and_records_its_size()
    {
        var (clusterer, texts) = await ClusterAsync(3);
        var chat = new FakeChatClient((_, _) => """{"label": "Broken heating"}""");
        var labeler = new ChatClientLabeler(chat, texts);

        var labeled = await labeler.LabelAsync(clusterer, [1, 1, 404]);

        Assert.Equal([1L], labeled);
        var cluster = await clusterer.GetClusterAsync(1);
        Assert.Equal(("Broken heating", 3), (cluster!.Label, cluster.LabeledAtCount));
        Assert.Contains("The heating is broken, report 0", Assert.Single(chat.Requests).Messages[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Small_clusters_wait_and_labeled_ones_are_labeled_again_once_they_double()
    {
        var (clusterer, texts) = await ClusterAsync(2);
        var labeler = new ChatClientLabeler(new FakeChatClient((_, _) => """{"label": "Broken heating"}"""), texts);

        Assert.Empty(await labeler.LabelAllAsync(clusterer));

        await clusterer.AddAsync([new ClusterItem("t2", new float[] { 1, 0.02f })]);
        texts.Set("t2", "Heat is out");
        Assert.Equal([1L], await labeler.LabelAllAsync(clusterer));
        Assert.False(labeler.IsDue((await clusterer.GetClusterAsync(1))!));

        await clusterer.AddAsync([.. Enumerable.Range(3, 3).Select(i => new ClusterItem($"t{i}", new float[] { 1, i * 0.01f }))]);
        Assert.True(labeler.IsDue((await clusterer.GetClusterAsync(1))!));
    }

    [Theory]
    [InlineData("<think>The texts are about heat. A short title.</think>\n{\"label\": \"Broken heating\"}")]
    [InlineData("<thinking>Heating, clearly.</thinking> Broken heating")]
    [InlineData("```json\n{\"label\": \"Broken heating\"}\n```")]
    public async Task A_label_is_read_past_a_model_s_reasoning_and_code_fence(string answer)
    {
        var (clusterer, texts) = await ClusterAsync(3);
        var labeler = new ChatClientLabeler(new FakeChatClient((_, _) => answer), texts);

        Assert.Equal([1L], await labeler.LabelAllAsync(clusterer));
        Assert.Equal("Broken heating", (await clusterer.GetClusterAsync(1))!.Label);
    }

    [Theory]
    [InlineData("""{"label": ""}""")]
    [InlineData("""{"label": "line one\nline two"}""")]
    [InlineData("""{"title": "wrong key"}""")]
    [InlineData("{not json}")]
    [InlineData("")]
    public async Task An_unusable_label_leaves_the_cluster_unlabeled(string answer)
    {
        var (clusterer, texts) = await ClusterAsync(3);
        var labeler = new ChatClientLabeler(new FakeChatClient((_, _) => answer), texts);

        Assert.Empty(await labeler.LabelAllAsync(clusterer));
        Assert.Null((await clusterer.GetClusterAsync(1))!.Label);
    }

    [Fact]
    public async Task A_bare_label_is_accepted_and_one_too_long_is_not()
    {
        var labeler = new ChatClientLabeler(new FakeChatClient((_, _) => "\"Broken heating\""), new InMemoryItemTextStore());
        var strict = new ChatClientLabeler(new FakeChatClient((_, _) => new string('x', 50)), new InMemoryItemTextStore(), new ChatClientLabelerOptions { MaxLength = 20 });

        Assert.Equal("Broken heating", await labeler.WriteLabelAsync(["a"]));
        Assert.Null(await strict.WriteLabelAsync(["a"]));
    }

    [Fact]
    public async Task A_failing_model_or_missing_texts_leave_labels_as_they_were()
    {
        var (clusterer, texts) = await ClusterAsync(3);
        var failing = new ChatClientLabeler(new FakeChatClient((_, _) => "") { Throw = new HttpRequestException("down") }, texts);
        var blind = new ChatClientLabeler(new FakeChatClient((_, _) => """{"label": "x"}"""), new InMemoryItemTextStore());

        Assert.Empty(await failing.LabelAllAsync(clusterer));
        Assert.Empty(await blind.LabelAllAsync(clusterer));
    }

    [Fact]
    public async Task Labeling_turns_tools_off()
    {
        var chat = new FakeChatClient((_, _) => """{"label": "Broken heating"}""");
        var labeler = new ChatClientLabeler(chat, new InMemoryItemTextStore());

        await labeler.WriteLabelAsync(["a"]);

        var options = Assert.Single(chat.Requests).Options!;
        Assert.Null(options.Tools);
        Assert.Equal(Microsoft.Extensions.AI.ChatToolMode.None, options.ToolMode);
    }

    [Fact]
    public void Invalid_options_are_refused()
    {
        var options = new ChatClientLabelerOptions { Instructions = "", MinClusterSize = 0, RelabelGrowth = 1, ExamplesPerLabel = 0, MaxLength = 5, MaxTextLength = 0 };

        var exception = Assert.Throws<ArgumentException>(() => new ChatClientLabeler(new FakeChatClient((_, _) => ""), new InMemoryItemTextStore(), options));

        foreach (var name in new[] { "Instructions", "MinClusterSize", "RelabelGrowth", "ExamplesPerLabel", "MaxLength", "MaxTextLength" })
        {
            Assert.Contains(name, exception.Message, StringComparison.Ordinal);
        }
    }
}
