using Microsoft.Extensions.AI;
using OllamaSharp;

namespace Akinly.AI.Tests;

/// <summary>
/// The whole pipeline against a local Ollama: real embeddings, a real model as judge and
/// labeler. Runs only when AKINLY_OLLAMA is set (to the server's address, or to 1 for
/// http://localhost:11434); the models come from AKINLY_OLLAMA_EMBED (default bge-m3) and
/// AKINLY_OLLAMA_CHAT (default qwen3.5:9b), which must be pulled.
/// </summary>
public sealed class OllamaEndToEndTests
{
    private static readonly string? Server = Environment.GetEnvironmentVariable("AKINLY_OLLAMA");

    private static Uri Address => Server is null or "1" ? new Uri("http://localhost:11434") : new Uri(Server);

    private static string EmbeddingModel => Environment.GetEnvironmentVariable("AKINLY_OLLAMA_EMBED") ?? "bge-m3";

    private static string ChatModel => Environment.GetEnvironmentVariable("AKINLY_OLLAMA_CHAT") ?? "qwen3.5:9b";

    [Fact]
    public async Task A_local_model_groups_texts_by_the_problem_they_describe()
    {
        Assert.SkipWhen(Server is null, "Set AKINLY_OLLAMA to run against a local Ollama.");
        using var embedder = new OllamaApiClient(Address, EmbeddingModel);
        using var chat = new OllamaApiClient(Address, ChatModel);

        // A wide ask zone, so the model decides nearly every placement.
        var options = new ClusteringOptions { EmbeddingModel = EmbeddingModel, JoinSimilarity = 0.95, CheckSimilarity = 0.3, MergeSimilarity = 0.97, MergeCheckSimilarity = 0.8 };
        var textOptions = new TextClustererOptions { Judge = { ChatOptions = new ChatOptions { ModelId = ChatModel } }, Labeler = { ChatOptions = new ChatOptions { ModelId = ChatModel } } };
        var clusterer = new TextClusterer(embedder, chat, options, textOptions);

        await clusterer.AddAsync(
        [
            new TextItem("heat-1", "The heating in my apartment has been broken for two weeks."),
            new TextItem("rent-1", "My landlord raised the rent by 30 percent this year."),
            new TextItem("wifi-1", "The building's wifi drops every evening."),
            new TextItem("heat-2", "There is no heat in our unit and it is freezing."),
            new TextItem("rent-2", "I can't afford the new rent after the increase."),
            new TextItem("wifi-2", "Internet in the apartment keeps disconnecting at night."),
            new TextItem("heat-3", "Our radiators are cold and the landlord won't fix them."),
            new TextItem("rent-3", "The rent went up again and I may have to move out."),
        ], TestContext.Current.CancellationToken);

        var groups = new List<string[]>();
        await foreach (var cluster in clusterer.ListClustersAsync(TestContext.Current.CancellationToken))
        {
            var members = await clusterer.GetMembersAsync(cluster.Id, TestContext.Current.CancellationToken);
            groups.Add([.. members.Select(m => m.Split('-')[0]).Distinct()]);
        }

        // Every cluster holds one problem, and each problem with three reports is one cluster.
        Assert.All(groups, g => Assert.Single(g));
        Assert.Equal(1, groups.Count(g => g[0] == "heat"));
        Assert.Equal(1, groups.Count(g => g[0] == "rent"));
    }
}
