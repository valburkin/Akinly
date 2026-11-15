using Akinly.Storage;
using Microsoft.Extensions.DependencyInjection;
using static Akinly.Tests.TestVectors;

namespace Akinly.Tests;

public sealed class MergePolicyTests
{
    /// <summary>
    /// Cluster 1 holds two items at 0 degrees, cluster 2 one item at 30 (similarity 0.87): below
    /// the join threshold of 0.9, at or above a merge threshold of 0.85.
    /// </summary>
    private static async Task<Clusterer> TwoClustersAsync(IMergePolicy? policy, ClusteringOptions? options = null)
    {
        var clusterer = new Clusterer(new InMemoryClusterStore(), options ?? ClusteringOptions.CosineOnly(Model, 0.9, 0.85), mergePolicy: policy);
        await clusterer.AddAsync([Item("a", At(0)), Item("a2", At(1)), Item("b", At(30))]);
        Assert.Equal(2L, await clusterer.GetClusterOfAsync("b"));
        return clusterer;
    }

    [Fact]
    public async Task Without_a_policy_the_larger_cluster_keeps_its_id()
    {
        var clusterer = await TwoClustersAsync(null);

        var step = await clusterer.MaintainAsync();

        Assert.Equal(new ClusterMerge(2, 1, PlacementReason.Similarity), Assert.Single(step.Merges));
    }

    [Fact]
    public async Task A_pair_the_policy_forbids_never_merges()
    {
        var asked = new List<(long, long)>();
        var clusterer = await TwoClustersAsync(new DelegateMergePolicy((first, second) =>
        {
            asked.Add((first.Id, second.Id));
            return MergeRule.Never;
        }));

        var step = await clusterer.MaintainAsync();

        Assert.Empty(step.Merges);
        Assert.Equal(2, await clusterer.ListClustersAsync().CountAsync());
        Assert.Equal([(1L, 2L)], asked.Distinct());   // the lower id first
    }

    [Theory]
    [InlineData(MergeRule.KeepFirst, 2L, 1L)]
    [InlineData(MergeRule.KeepSecond, 1L, 2L)]
    public async Task The_policy_chooses_which_cluster_keeps_its_id(MergeRule rule, long from, long into)
    {
        var clusterer = await TwoClustersAsync(new DelegateMergePolicy((_, _) => rule));

        var step = await clusterer.MaintainAsync();

        Assert.Equal(new ClusterMerge(from, into, PlacementReason.Similarity), Assert.Single(step.Merges));
        Assert.Equal(into, await clusterer.GetClusterOfAsync("a"));
        Assert.Equal(3, (await clusterer.GetClusterAsync(into))!.Count);
    }

    [Fact]
    public async Task No_merge_question_is_asked_about_a_pair_that_may_never_merge()
    {
        // Join at 0.9, ask from 0.6; merge at 0.95, ask from 0.8: the pair at 0.87 is a close call.
        // Refusals are not to bind merges here, so only the policy keeps the pair apart.
        var options = Judged(o => o.MergeRespectsRefusals = false);
        var judge = new DelegateJudge(_ => VerdictKind.Different);
        var allowed = new Clusterer(new InMemoryClusterStore(), options);
        var forbidden = new Clusterer(new InMemoryClusterStore(), options, mergePolicy: new DelegateMergePolicy((_, _) => MergeRule.Never));
        foreach (var clusterer in new[] { allowed, forbidden })
        {
            await clusterer.SettleAsync([Item("a", At(0)), Item("b", At(30))], judge);
        }

        Assert.Single((await allowed.MaintainAsync()).Questions.OfType<MergeQuestion>());
        Assert.Empty((await forbidden.MaintainAsync()).Questions);
    }

    [Fact]
    public async Task A_judge_s_late_yes_does_not_merge_a_pair_the_policy_has_since_forbidden()
    {
        var forbid = false;
        var clusterer = new Clusterer(new InMemoryClusterStore(), Judged(o => o.MergeRespectsRefusals = false), mergePolicy: new DelegateMergePolicy((_, _) => forbid ? MergeRule.Never : MergeRule.Allow));
        await clusterer.SettleAsync([Item("a", At(0)), Item("b", At(30))], new DelegateJudge(_ => VerdictKind.Different));
        var question = Assert.Single((await clusterer.MaintainAsync()).Questions.OfType<MergeQuestion>());

        forbid = true;
        var step = await clusterer.AnswerAsync([new Verdict(question.Id, VerdictKind.Same)]);

        Assert.Empty(step.Merges);
        Assert.Equal(2, await clusterer.ListClustersAsync().CountAsync());
    }

    [Fact]
    public async Task An_answer_outside_the_rules_fails_the_call()
    {
        var clusterer = await TwoClustersAsync(new DelegateMergePolicy((_, _) => (MergeRule)42));

        await Assert.ThrowsAsync<InvalidOperationException>(() => clusterer.MaintainAsync());
    }

    [Fact]
    public async Task The_asynchronous_form_receives_the_call_s_cancellation_token()
    {
        using var cancel = new CancellationTokenSource();
        CancellationToken seen = default;
        var clusterer = await TwoClustersAsync(new DelegateMergePolicy((_, _, token) =>
        {
            seen = token;
            return ValueTask.FromResult(MergeRule.Allow);
        }));

        await clusterer.MaintainAsync(cancel.Token);

        Assert.Equal(cancel.Token, seen);
        Assert.Throws<ArgumentNullException>(() => new DelegateMergePolicy((Func<Cluster, Cluster, MergeRule>)null!));
        Assert.Throws<ArgumentNullException>(() => new DelegateMergePolicy((Func<Cluster, Cluster, CancellationToken, ValueTask<MergeRule>>)null!));
    }

    [Fact]
    public async Task Dependency_injection_gives_the_clusterer_the_registered_policy()
    {
        foreach (var register in new Action<AkinlyBuilder>[]
        {
            builder => builder.UseMergePolicy(new DelegateMergePolicy((_, _) => MergeRule.Never)),
            builder => builder.UseMergePolicy<NeverMerge>(),
        })
        {
            var builder = new ServiceCollection().AddAkinly(o =>
            {
                o.EmbeddingModel = Model;
                (o.JoinSimilarity, o.CheckSimilarity, o.MergeSimilarity, o.MergeCheckSimilarity) = (0.9, 0.9, 0.85, 0.85);
            });
            register(builder);
            using var provider = builder.Services.BuildServiceProvider();
            var clusterer = provider.GetRequiredService<Clusterer>();
            await clusterer.AddAsync([Item("a", At(0)), Item("b", At(30))]);

            Assert.Empty((await clusterer.MaintainAsync()).Merges);
        }

        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddAkinly().UseMergePolicy(null!));
    }

    private sealed class NeverMerge : IMergePolicy
    {
        public ValueTask<MergeRule> DecideAsync(Cluster first, Cluster second, CancellationToken cancellationToken) =>
            ValueTask.FromResult(MergeRule.Never);
    }
}
