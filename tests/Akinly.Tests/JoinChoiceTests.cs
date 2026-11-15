using Akinly.Storage;
using static Akinly.Tests.TestVectors;

namespace Akinly.Tests;

/// <summary>Join questions with choices: the judge picks one of the nearest clusters, or none.</summary>
public sealed class JoinChoiceTests
{
    private static Clusterer Create(int choices = 3, int maxChecks = 5) =>
        new(new InMemoryClusterStore(), Judged(o =>
        {
            o.JoinChoices = choices;
            o.MaxChecksPerItem = maxChecks;
        }));

    // Clusters 1 (left, at -40°) and 2 (right, at 40°); x at -5° is in both ask zones, nearer left.
    private static async Task<(Clusterer Clusterer, JoinQuestion Question)> AskAsync(int choices = 3, int maxChecks = 5)
    {
        var clusterer = Create(choices, maxChecks);
        await clusterer.AddAsync([Item("left", At(-40)), Item("right", At(40))]);
        var question = Assert.IsType<JoinQuestion>(Assert.Single((await clusterer.AddAsync([Item("x", At(-5))])).Questions));
        return (clusterer, question);
    }

    [Fact]
    public async Task A_question_offers_the_nearest_clusters_nearest_first()
    {
        var (_, question) = await AskAsync();

        Assert.Equal(1, question.ClusterId);
        var other = Assert.Single(question.OtherChoices);
        Assert.Equal(2, other.ClusterId);
        Assert.True(question.Similarity > other.Similarity);
        Assert.Equal(["right"], other.ExampleItemIds);
    }

    [Fact]
    public async Task One_choice_asks_yes_or_no()
    {
        var (_, question) = await AskAsync(choices: 1);

        Assert.Empty(question.OtherChoices);
    }

    [Fact]
    public async Task Picking_another_cluster_joins_it()
    {
        var (clusterer, question) = await AskAsync();

        var step = await clusterer.AnswerAsync([Verdict.Picked(question.Id, 2)]);

        Assert.Equal([new Placement("x", 2, PlacementReason.Judge)], step.Placements);
    }

    [Fact]
    public async Task Same_joins_the_nearest_cluster()
    {
        var (clusterer, question) = await AskAsync();

        var step = await clusterer.AnswerAsync([Verdict.Same(question.Id)]);

        Assert.Equal([new Placement("x", 1, PlacementReason.Judge)], step.Placements);
    }

    [Fact]
    public async Task None_refuses_every_cluster_offered()
    {
        var (clusterer, question) = await AskAsync();

        var step = await clusterer.AnswerAsync([Verdict.Different(question.Id)]);

        Assert.Empty(step.Questions);
        Assert.Equal([new Placement("x", 3, PlacementReason.NewCluster)], step.Placements);
        Assert.Equal([1L, 2L], (await clusterer.Store.GetClustersAsync([3], default))[0].RefusedBy.Order());
    }

    [Fact]
    public async Task A_pick_that_was_not_offered_is_a_failed_answer()
    {
        var (clusterer, question) = await AskAsync();

        var step = await clusterer.AnswerAsync([Verdict.Picked(question.Id, 99)]);

        Assert.Empty(step.Placements);
        Assert.Equal([question.Id], (await clusterer.GetOpenQuestionsAsync()).Select(q => q.Id));
    }

    [Fact]
    public async Task Settling_with_a_judge_keeps_its_pick()
    {
        var clusterer = Create();
        await clusterer.AddAsync([Item("left", At(-40)), Item("right", At(40))]);
        await clusterer.AddAsync([Item("x", At(-5))]);

        await clusterer.SettleAsync(new PickJudge(2));

        Assert.Equal(2, (await clusterer.GetClusterAsync(2))!.Count);
        Assert.Equal(1, (await clusterer.GetClusterAsync(1))!.Count);
    }

    // Picks the given cluster when it is offered, and says "different" otherwise.
    private sealed class PickJudge(long clusterId) : IClusterJudge
    {
        public Task<IReadOnlyList<Verdict>> JudgeAsync(IReadOnlyList<Question> questions, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Verdict>>([.. questions.Select(q => q is JoinQuestion join && (join.ClusterId == clusterId || join.OtherChoices.Any(c => c.ClusterId == clusterId))
                ? Verdict.Picked(q.Id, clusterId)
                : Verdict.Different(q.Id))]);
    }

    [Fact]
    public async Task A_question_offers_no_more_clusters_than_the_checks_left()
    {
        var (_, question) = await AskAsync(choices: 3, maxChecks: 1);

        Assert.Empty(question.OtherChoices);
    }

    [Fact]
    public async Task A_cluster_with_nothing_to_show_is_not_offered_as_a_choice()
    {
        var clusterer = new Clusterer(new InMemoryClusterStore(), Judged(o =>
        {
            o.JoinChoices = 3;
            o.ExamplesPerQuestion = 0;
        }));
        await clusterer.AddAsync([Item("left", At(-40)), Item("right", At(40))]);
        await clusterer.SetLabelAsync(1, "left things");

        var question = Assert.IsType<JoinQuestion>(Assert.Single((await clusterer.AddAsync([Item("x", At(-5))])).Questions));

        Assert.Equal(1, question.ClusterId);
        Assert.Empty(question.OtherChoices);
    }

    [Fact]
    public async Task A_failed_answer_keeps_the_question_open_and_unanswerable_lets_similarity_decide()
    {
        var (clusterer, question) = await AskAsync();

        Assert.Empty((await clusterer.AnswerAsync([Verdict.Failed(question.Id)])).Placements);
        Assert.Equal([question.Id], (await clusterer.GetOpenQuestionsAsync()).Select(q => q.Id));

        var step = await clusterer.AnswerAsync([Verdict.Unanswerable(question.Id)]);
        Assert.Equal([new Placement("x", 3, PlacementReason.NewCluster)], step.Placements);
    }

    [Fact]
    public async Task Picking_the_question_s_own_cluster_is_the_same_as_same()
    {
        var (clusterer, question) = await AskAsync();

        var step = await clusterer.AnswerAsync([Verdict.Picked(question.Id, question.ClusterId)]);

        Assert.Equal([new Placement("x", 1, PlacementReason.Judge)], step.Placements);
    }

    [Fact]
    public async Task A_picked_cluster_that_merged_since_is_followed_to_the_one_it_merged_into()
    {
        var clusterer = new Clusterer(new InMemoryClusterStore(), Judged(o =>
        {
            o.JoinChoices = 3;
            o.JoinSimilarity = 0.97;
            o.MergeSimilarity = o.MergeCheckSimilarity = 0.95;
        }));
        await clusterer.AddAsync([Item("a", At(0))]);
        await clusterer.AnswerAsync([Verdict.Unanswerable((await clusterer.AddAsync([Item("b", At(15))])).Questions[0].Id)]);
        await clusterer.AddAsync([Item("b2", At(16))]);   // cluster 2 has two members, cluster 1 one
        var question = Assert.IsType<JoinQuestion>((await clusterer.AddAsync([Item("x", At(-16))])).Questions[0]);
        Assert.Equal(1, question.ClusterId);
        Assert.Equal(2, Assert.Single(question.OtherChoices).ClusterId);

        Assert.Equal(new ClusterMerge(1, 2, PlacementReason.Similarity), Assert.Single((await clusterer.MaintainAsync()).Merges));
        var step = await clusterer.AnswerAsync([Verdict.Picked(question.Id, 1)]);

        Assert.Equal([new Placement("x", 2, PlacementReason.Judge)], step.Placements);
    }

    [Fact]
    public async Task Choices_survive_an_export_and_import()
    {
        var store = new InMemoryClusterStore();
        var clusterer = new Clusterer(store, Judged(o => o.JoinChoices = 3));
        await clusterer.AddAsync([Item("left", At(-40)), Item("right", At(40))]);
        var asked = Assert.IsType<JoinQuestion>(Assert.Single((await clusterer.AddAsync([Item("x", At(-5))])).Questions));

        using var stream = new MemoryStream();
        await store.ExportAsync(stream);
        stream.Position = 0;
        var imported = await InMemoryClusterStore.ImportAsync(stream);

        var question = Assert.IsType<JoinQuestion>(Assert.Single(await imported.GetOpenQuestionsAsync(default)).Question);
        var (before, after) = (Assert.Single(asked.OtherChoices), Assert.Single(question.OtherChoices));
        Assert.Equal((before.ClusterId, before.Similarity, before.ClusterLabel), (after.ClusterId, after.Similarity, after.ClusterLabel));
        Assert.Equal(before.ExampleItemIds, after.ExampleItemIds);
    }
}
