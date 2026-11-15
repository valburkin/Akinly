using Akinly.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Akinly.Tests.DependencyInjection;

public sealed class AkinlyServiceCollectionExtensionsTests
{
    [Fact]
    public void AddAkinly_registers_a_singleton_clusterer_over_an_in_memory_store()
    {
        using var provider = new ServiceCollection()
            .AddAkinly(o => o.EmbeddingModel = TestVectors.Model)
            .Services.BuildServiceProvider();

        var clusterer = provider.GetRequiredService<Clusterer>();

        Assert.Same(clusterer, provider.GetRequiredService<Clusterer>());
        Assert.IsType<InMemoryClusterStore>(clusterer.Store);
        Assert.Equal(TestVectors.Model, clusterer.Options.EmbeddingModel);
    }

    [Fact]
    public void Options_bind_from_configuration_and_can_be_adjusted()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Akinly:EmbeddingModel"] = "text-embedding-3-small",
                ["Akinly:JoinSimilarity"] = "0.8",
                ["Akinly:FailedAnswers:MaxAttempts"] = "5",
            })
            .Build();
        using var provider = new ServiceCollection()
            .AddAkinly(configuration.GetSection(ClusteringOptions.SectionName), o => o.MaxOpenQuestions = 50)
            .Services.BuildServiceProvider();

        var options = provider.GetRequiredService<Clusterer>().Options;

        Assert.Equal(("text-embedding-3-small", 0.8, 5, 50), (options.EmbeddingModel, options.JoinSimilarity, options.FailedAnswers.MaxAttempts, options.MaxOpenQuestions));
    }

    [Fact]
    public void Invalid_options_fail_validation_with_every_problem()
    {
        using var provider = new ServiceCollection()
            .AddAkinly(o => o.JoinSimilarity = 2)
            .Services.BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<ClusteringOptions>>().Value);

        Assert.Contains(exception.Failures, f => f.Contains("EmbeddingModel", StringComparison.Ordinal));
        Assert.Contains(exception.Failures, f => f.Contains("JoinSimilarity", StringComparison.Ordinal));
    }

    [Fact]
    public void The_builder_swaps_the_store_and_registers_a_judge()
    {
        var store = new InMemoryClusterStore();
        var judge = new DelegateJudge(_ => VerdictKind.Same);
        var services = new ServiceCollection();
        services.AddAkinly(o => o.EmbeddingModel = TestVectors.Model).UseStore(store).UseJudge(judge);
        using var provider = services.BuildServiceProvider();

        Assert.Same(store, provider.GetRequiredService<Clusterer>().Store);
        Assert.Same(judge, provider.GetRequiredService<IClusterJudge>());

        var typed = new ServiceCollection();
        typed.AddAkinly(o => o.EmbeddingModel = TestVectors.Model).UseStore<InMemoryClusterStore>().UseJudge<NoJudge>().UseInMemoryStore();
        using var typedProvider = typed.BuildServiceProvider();
        Assert.IsType<NoJudge>(typedProvider.GetRequiredService<IClusterJudge>());
        Assert.IsType<InMemoryClusterStore>(typedProvider.GetRequiredService<IClusterStore>());
    }

    private sealed class NoJudge : IClusterJudge
    {
        public Task<IReadOnlyList<Verdict>> JudgeAsync(IReadOnlyList<Question> questions, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Verdict>>([]);
    }
}
