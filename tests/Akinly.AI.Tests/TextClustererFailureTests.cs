using System.Reflection;
using System.Text.Json;
using Akinly.Storage;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Akinly.AI.Tests;

/// <summary>Failure modes of the text clusterer that real setups hit.</summary>
public sealed class TextClustererFailureTests
{
    [Fact]
    public async Task Concurrent_adds_run_one_at_a_time_so_no_question_is_paid_for_twice()
    {
        var chat = new FakeChatClient((_, _) => """{"same": false}""") { Delay = TimeSpan.FromMilliseconds(20) };
        var options = new ClusteringOptions { JoinSimilarity = 1, CheckSimilarity = 0.1, MergeSimilarity = 1, MergeCheckSimilarity = 1 };
        var clusterer = new TextClusterer(new FakeEmbedder("heat"), chat, options, new TextClustererOptions { LabelClusters = false });
        await clusterer.AddAsync([new TextItem("seed", "heat one")]);

        await Task.WhenAll(
            clusterer.AddAsync([new TextItem("a", "heat two")]),
            clusterer.AddAsync([new TextItem("b", "heat three")]));

        var questions = chat.Requests.Select(r => JsonDocument.Parse(r.Messages[1].Text).RootElement.GetProperty("new_text").GetString()).ToList();
        Assert.Equal(questions.Count, questions.Distinct().Count());
    }

    [Fact]
    public async Task Repeated_ids_are_refused_before_anything_is_embedded()
    {
        var embedder = new FakeEmbedder("heat");
        var clusterer = new TextClusterer(embedder, null, ClusteringOptions.CosineOnly("m", 0.9));

        await Assert.ThrowsAsync<ArgumentException>(() => clusterer.AddAsync([new TextItem("a", "x"), new TextItem("a", "y")]));

        Assert.Empty(embedder.BatchSizes);
    }

    [Fact]
    public async Task An_add_the_clusterer_refuses_leaves_no_texts_behind()
    {
        var clusterer = new TextClusterer(new FakeEmbedder("heat"), null, ClusteringOptions.CosineOnly("m", 0.9));
        await clusterer.AddAsync([new TextItem("a", "heat")]);

        await Assert.ThrowsAsync<ArgumentException>(() => clusterer.AddAsync([new TextItem("b", "heat again"), new TextItem("a", "heat twice")]));

        Assert.Null(await clusterer.Texts.GetTextAsync("b", default));
        Assert.Equal("heat", await clusterer.Texts.GetTextAsync("a", default));
    }

    [Fact]
    public async Task An_add_the_store_fails_partway_keeps_its_texts_for_the_write_it_may_finish()
    {
        var store = DispatchProxy.Create<IClusterStore, TimingOutStore>();
        ((TimingOutStore)(object)store).Inner = new InMemoryClusterStore();
        var clusterer = new TextClusterer(new FakeEmbedder("heat"), null, ClusteringOptions.CosineOnly("m", 0.9), store: store);

        await Assert.ThrowsAsync<TimeoutException>(() => clusterer.AddAsync([new TextItem("a", "heat")]));

        Assert.Equal("heat", await clusterer.Texts.GetTextAsync("a", default));
    }

    [Fact]
    public async Task A_text_store_of_your_own_is_used_by_every_part()
    {
        var mine = new CountingTexts();
        var services = new ServiceCollection();
        services.AddSingleton<IItemTextStore>(mine);
        services.AddSingleton<IChatClient>(new FakeChatClient((_, _) => """{"same": true}"""));
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new FakeEmbedder("heat"));
        services.AddAkinly(o =>
            {
                o.EmbeddingModel = FakeEmbedder.ModelId;
                o.JoinSimilarity = 1;
                o.CheckSimilarity = 0.1;
            })
            .UseChatClientJudge()
            .AddTextClusterer();
        using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<TextClusterer>().AddAsync([new TextItem("a", "heat one"), new TextItem("b", "heat 2")]);   // close, not identical: a question

        Assert.Equal(2, mine.Writes);
        Assert.True(mine.Reads > 0);
        Assert.Same(mine, provider.GetRequiredService<IItemTextSource>());
    }

    [Fact]
    public void Invalid_adapter_options_fail_when_registered()
    {
        var builder = new ServiceCollection().AddAkinly(o => o.EmbeddingModel = "m");

        Assert.Throws<ArgumentException>(() => builder.UseChatClientJudge(o => o.MaxParallelRequests = 0));
        Assert.Throws<ArgumentException>(() => builder.AddChatClientLabeler(o => o.MinClusterSize = 0));
    }

    [Fact]
    public async Task A_keyed_chat_client_can_serve_the_judge()
    {
        var cheap = new FakeChatClient((_, _) => """{"same": true}""");
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IChatClient>("cheap", cheap);
        services.AddAkinly(o => o.EmbeddingModel = "m").UseChatClientJudge(chatClientKey: "cheap");
        using var provider = services.BuildServiceProvider();
        var texts = provider.GetRequiredService<IItemTextStore>();
        await texts.SetTextAsync("a", "x", default);
        await texts.SetTextAsync("m", "y", default);

        await provider.GetRequiredService<IClusterJudge>().JudgeAsync([new JoinQuestion(new QuestionId(1), "a", 1, 0.7, null, ["m"])], default);

        Assert.Single(cheap.Requests);
    }

    private sealed class CountingTexts : IItemTextStore
    {
        private readonly InMemoryItemTextStore _inner = new();

        public int Reads { get; private set; }

        public int Writes { get; private set; }

        public ValueTask<string?> GetTextAsync(string itemId, CancellationToken cancellationToken)
        {
            Reads++;
            return _inner.GetTextAsync(itemId, cancellationToken);
        }

        public ValueTask SetTextAsync(string itemId, string text, CancellationToken cancellationToken)
        {
            Writes++;
            return _inner.SetTextAsync(itemId, text, cancellationToken);
        }

        public ValueTask<bool> RemoveTextAsync(string itemId, CancellationToken cancellationToken) => _inner.RemoveTextAsync(itemId, cancellationToken);
    }

    /// <summary>A store whose writes time out after they may have reached the database.</summary>
    public class TimingOutStore : DispatchProxy
    {
        public IClusterStore Inner { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod!.Name == nameof(IClusterStore.ApplyAsync)
                ? Task.FromException(new TimeoutException("The database did not answer in time."))
                : targetMethod.Invoke(Inner, args);
    }
}
