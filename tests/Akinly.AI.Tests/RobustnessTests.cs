using Akinly.Storage;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Akinly.AI.Tests;

/// <summary>Interruptions, extreme settings and odd answers.</summary>
public sealed class RobustnessTests
{
    [Theory]
    [InlineData("""<thinking>maybe {"same": true}</thinking> No""", false)]
    [InlineData("""<reasoning>{"same": true}</reasoning>{"same": false}""", false)]
    [InlineData("""I think { is odd. {"same": false}""", false)]
    [InlineData("""The brace "{" is odd. {"same": true}""", true)]
    [InlineData("""{"outer": {"inner": 1}, "same": true}""", true)]
    [InlineData("**No**", false)]
    [InlineData("Yes!", true)]
    public void Reasoning_tags_stray_braces_and_emphasis_do_not_hide_the_answer(string text, bool expected)
    {
        Assert.True(JudgePrompts.TryParse(text, out var same));
        Assert.Equal(expected, same);
    }

    [Fact]
    public void A_timeout_too_long_to_schedule_is_refused()
    {
        Assert.Contains("RequestTimeout", new ChatClientJudgeOptions { RequestTimeout = TimeSpan.FromDays(30) }.Validate().Single(), StringComparison.Ordinal);
        Assert.Contains("RequestTimeout", new ChatClientLabelerOptions { RequestTimeout = TimeSpan.FromDays(30) }.Validate().Single(), StringComparison.Ordinal);
        Assert.Empty(new ChatClientJudgeOptions { RequestTimeout = TimeSpan.FromDays(20) }.Validate());
    }

    [Fact]
    public async Task An_add_interrupted_while_keeping_texts_leaves_none_behind()
    {
        var texts = new FailingTexts(failOnWrite: 2);
        var clusterer = new TextClusterer(new FakeEmbedder("heat"), null, ClusteringOptions.CosineOnly("m", 0.9), texts: texts);

        await Assert.ThrowsAsync<OperationCanceledException>(() => clusterer.AddAsync([new TextItem("a", "heat"), new TextItem("b", "heat 2"), new TextItem("c", "heat 33")]));

        Assert.Equal(0, texts.Count);
        Assert.Empty(await clusterer.ListClustersAsync().ToListAsync());
    }

    [Fact]
    public async Task A_removal_forgets_texts_before_it_removes_the_items()
    {
        var texts = new FailingTexts(failOnWrite: int.MaxValue);
        var clusterer = new TextClusterer(new FakeEmbedder("heat"), null, ClusteringOptions.CosineOnly("m", 0.9), texts: texts);
        await clusterer.AddAsync([new TextItem("a", "heat"), new TextItem("b", "heat 2")]);
        texts.OnRemove = async id => texts.PlacedWhenForgotten.Add(await clusterer.Clusterer.GetClusterOfAsync(id) is not null);

        await clusterer.RemoveAsync(["a", "b"]);

        Assert.Equal([true, true], texts.PlacedWhenForgotten);
        Assert.Equal(0, texts.Count);
        Assert.Null(await clusterer.Clusterer.GetClusterOfAsync("a"));
    }

    [Fact]
    public void A_read_only_text_source_alone_is_refused_at_registration()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IItemTextSource>(new InMemoryItemTextStore());

        Assert.Throws<InvalidOperationException>(() => services.AddAkinly(o => o.EmbeddingModel = "m").UseChatClientJudge());
    }

    [Fact]
    public async Task A_text_store_registered_after_AddAkinly_is_still_the_one_used()
    {
        var mine = new InMemoryItemTextStore();
        var services = new ServiceCollection();
        services.AddSingleton<IChatClient>(new FakeChatClient((_, _) => """{"same": true}"""));
        services.AddAkinly(o => o.EmbeddingModel = "m").UseChatClientJudge();
        services.AddSingleton<IItemTextStore>(mine);
        using var provider = services.BuildServiceProvider();

        Assert.Same(mine, provider.GetRequiredService<IItemTextStore>());
        Assert.Same(mine, provider.GetRequiredService<IItemTextSource>());
    }

    [Fact]
    public async Task A_label_written_for_a_cluster_that_merged_meanwhile_is_dropped()
    {
        var clusterer = new Clusterer(new InMemoryClusterStore(), ClusteringOptions.CosineOnly("m", 0.9, 0.5));
        await clusterer.AddAsync([.. Enumerable.Range(0, 3).Select(i => new ClusterItem($"a{i}", new float[] { 1, i * 0.01f })),
            .. Enumerable.Range(0, 4).Select(i => new ClusterItem($"b{i}", new float[] { 0.6f, 0.8f + (i * 0.01f) }))]);
        var texts = new InMemoryItemTextStore();
        foreach (var id in new[] { "a0", "a1", "a2", "b0", "b1", "b2", "b3" })
        {
            texts.Set(id, "text " + id);
        }

        // While the model writes cluster 1's label, cluster 1 merges into the larger cluster 2.
        var chat = new MergingChatClient(() => clusterer.MaintainAsync());
        var labeler = new ChatClientLabeler(chat, texts);

        var labeled = await labeler.LabelAsync(clusterer, [1]);

        Assert.Empty(labeled);
        Assert.Null((await clusterer.GetClusterAsync(2))!.Label);
    }

    /// <summary>A text store that fails a write with a cancellation, and can watch removals.</summary>
    private sealed class FailingTexts(int failOnWrite) : IItemTextStore
    {
        private readonly InMemoryItemTextStore _inner = new();
        private int _writes;

        public Func<string, Task>? OnRemove { get; set; }

        public List<bool> PlacedWhenForgotten { get; } = [];

        public int Count => _inner.Count;

        public ValueTask<string?> GetTextAsync(string itemId, CancellationToken cancellationToken) => _inner.GetTextAsync(itemId, cancellationToken);

        public ValueTask SetTextAsync(string itemId, string text, CancellationToken cancellationToken)
        {
            if (++_writes == failOnWrite)
            {
                throw new OperationCanceledException("Interrupted while keeping texts.");
            }

            return _inner.SetTextAsync(itemId, text, cancellationToken);
        }

        public async ValueTask<bool> RemoveTextAsync(string itemId, CancellationToken cancellationToken)
        {
            if (OnRemove is not null)
            {
                await OnRemove(itemId);
            }

            return await _inner.RemoveTextAsync(itemId, cancellationToken);
        }
    }

    /// <summary>A chat client that, while "writing" a label, makes something else happen first.</summary>
    private sealed class MergingChatClient(Func<Task> meanwhile) : IChatClient
    {
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            await meanwhile();
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, """{"label": "Written for the smaller cluster"}"""));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
