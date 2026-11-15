using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Akinly.AI.Tests;

public sealed class TextClustererTests
{
    /// <summary>
    /// A model that judges by keyword (same when the new text and a member share "heat" or
    /// "rent") and labels with the first keyword it sees.
    /// </summary>
    private static FakeChatClient Model() => new((messages, _) =>
    {
        var data = JsonDocument.Parse(messages[1].Text).RootElement;
        if (data.TryGetProperty("texts", out var texts))
        {
            var first = texts[0].GetString()!;
            return first.Contains("heat", StringComparison.OrdinalIgnoreCase) ? """{"label": "Broken heating"}""" : """{"label": "Rent too high"}""";
        }

        if (data.TryGetProperty("new_text", out var item))
        {
            var text = item.GetString()!;
            var group = string.Join(' ', data.GetProperty("group_members").EnumerateArray().Select(e => e.GetString()));
            var same = (text.Contains("heat", StringComparison.OrdinalIgnoreCase) && group.Contains("heat", StringComparison.OrdinalIgnoreCase))
                || (text.Contains("rent", StringComparison.OrdinalIgnoreCase) && group.Contains("rent", StringComparison.OrdinalIgnoreCase));
            return same ? """{"same": true}""" : """{"same": false}""";
        }

        return """{"same": false}""";
    });

    private static readonly TextItem[] Reports =
    [
        new("1", "The heat is out in my apartment"),
        new("2", "My heating has been broken for weeks"),
        new("3", "No heat since Monday, landlord ignores me"),
        new("4", "The rent went up by 30 percent"),
        new("5", "I can't pay the rent this month"),
        new("6", "Rent increase is too steep"),
    ];

    [Fact]
    public async Task Texts_in_clusters_out_with_close_calls_judged_and_clusters_labeled()
    {
        // Every text lands on nearly the same ray, so only the judge can tell heat from rent;
        // merges are off, so the two clusters stay apart.
        var embedder = new OneRayEmbedder();
        var options = new ClusteringOptions { JoinSimilarity = 1, CheckSimilarity = 0.6, MergeSimilarity = 1, MergeCheckSimilarity = 1 };
        var clusterer = new TextClusterer(embedder, Model(), options, new TextClustererOptions { EmbeddingBatchSize = 4 });

        await clusterer.AddAsync(Reports);

        var clusters = await clusterer.ListClustersAsync().ToListAsync();
        var byLabel = clusters.ToDictionary(c => c.Label ?? "(none)", c => c.Id);
        Assert.Equal(["1", "2", "3"], (await clusterer.GetMembersAsync(byLabel["Broken heating"])).Order());
        Assert.Equal(["4", "5", "6"], (await clusterer.GetMembersAsync(byLabel["Rent too high"])).Order());
        Assert.Equal([4, 2], embedder.BatchSizes);
        Assert.Equal(OneRayEmbedder.ModelId, clusterer.Clusterer.Options.EmbeddingModel);
        Assert.Equal(6, Assert.IsType<InMemoryItemTextStore>(clusterer.Texts).Count);
    }

    [Fact]
    public async Task Cosine_only_needs_no_chat_client_and_ask_zones_do()
    {
        var embedder = new FakeEmbedder("heat", "rent");
        var cosine = new TextClusterer(embedder, null, ClusteringOptions.CosineOnly("", 0.9));

        await cosine.AddAsync(Reports);

        Assert.Equal(2, await cosine.ListClustersAsync().CountAsync());
        Assert.Throws<ArgumentException>(() => new TextClusterer(embedder, null, new ClusteringOptions { EmbeddingModel = "m" }));
    }

    [Fact]
    public async Task Removing_forgets_texts_and_forgetting_keeps_the_item()
    {
        var clusterer = new TextClusterer(new FakeEmbedder("heat", "rent"), null, ClusteringOptions.CosineOnly("m", 0.9));
        await clusterer.AddAsync(Reports);

        await clusterer.RemoveAsync(["1"]);
        Assert.True(await clusterer.ForgetTextAsync("2"));
        Assert.False(await clusterer.ForgetTextAsync("2"));

        Assert.Null(await clusterer.Texts.GetTextAsync("1", default));
        Assert.Equal(["2", "3"], await clusterer.GetMembersAsync(1));
    }

    [Fact]
    public async Task Bad_input_is_refused_before_anything_is_embedded()
    {
        var embedder = new FakeEmbedder("heat");
        var clusterer = new TextClusterer(embedder, null, ClusteringOptions.CosineOnly("m", 0.9));

        await Assert.ThrowsAsync<ArgumentException>(() => clusterer.AddAsync([new TextItem("a", null!)]));
        Assert.Empty(embedder.BatchSizes);
        Assert.Throws<ArgumentException>(() => new TextClusterer(embedder, null, ClusteringOptions.CosineOnly("m", 0.9), new TextClustererOptions { EmbeddingBatchSize = 0 }));
    }

    [Fact]
    public async Task An_embedder_that_returns_the_wrong_number_of_vectors_is_caught()
    {
        var clusterer = new TextClusterer(new ShortEmbedder(), null, ClusteringOptions.CosineOnly("m", 0.9));

        await Assert.ThrowsAsync<InvalidOperationException>(() => clusterer.AddAsync(Reports));
    }

    [Fact]
    public async Task Dependency_injection_assembles_the_same_pipeline()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IChatClient>(Model());
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new OneRayEmbedder());
        services.AddAkinly(o =>
            {
                o.EmbeddingModel = OneRayEmbedder.ModelId;
                o.JoinSimilarity = 1;
                o.MergeSimilarity = 1;
                o.MergeCheckSimilarity = 1;
            })
            .UseChatClientJudge(o => o.MaxParallelRequests = 2)
            .AddChatClientLabeler(o => o.MinClusterSize = 2)
            .AddTextClusterer(o => o.EmbeddingBatchSize = 10);
        using var provider = services.BuildServiceProvider();

        var clusterer = provider.GetRequiredService<TextClusterer>();
        await clusterer.AddAsync(Reports);

        Assert.IsType<ChatClientJudge>(provider.GetRequiredService<IClusterJudge>());
        Assert.Same(clusterer.Texts, provider.GetRequiredService<IItemTextSource>());
        Assert.Equal(2, await clusterer.ListClustersAsync().CountAsync(c => c.Label is not null));
        Assert.Throws<ArgumentException>(() => new TextClusterer(
            provider.GetRequiredService<Clusterer>(), new FakeEmbedder(), new InMemoryItemTextStore(), judge: null, labeler: null));
    }

    /// <summary>Every text on nearly one ray: each a hair further along a second axis than the last.</summary>
    private sealed class OneRayEmbedder : IEmbeddingGenerator<string, Embedding<float>>
    {
        public const string ModelId = "one-ray";

        private int _count;

        public List<int> BatchSizes { get; } = [];

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            var texts = values.ToList();
            BatchSizes.Add(texts.Count);
            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
                texts.Select(_ => new Embedding<float>(new float[] { 1, ++_count * 0.001f }))));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(EmbeddingGeneratorMetadata) ? new EmbeddingGeneratorMetadata("fake", null, ModelId) : null;

        public void Dispose()
        {
        }
    }

    private sealed class ShortEmbedder : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 1, 0 })]));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
