using System.Text;
using Akinly.Storage;
using static Akinly.Tests.TestVectors;

namespace Akinly.Tests;

/// <summary>Edge cases of the clusterer's state machine: stray answers, merges under open questions, capacity, empty clusters.</summary>
public sealed class ClustererEdgeCaseTests
{
    [Fact]
    public async Task An_answer_to_a_question_not_yet_asked_is_not_applied_to_one_asked_later_in_the_call()
    {
        var clusterer = new Clusterer(new InMemoryClusterStore(), Judged());
        await clusterer.AddAsync([Item("left", At(-40)), Item("right", At(40))]);
        var first = (await clusterer.AddAsync([Item("x", At(-5))])).Questions[0];
        var next = new QuestionId(first.Id.Value + 1);

        var step = await clusterer.AnswerAsync([Verdict.Different(first.Id), Verdict.Same(next)]);

        var followUp = Assert.IsType<JoinQuestion>(Assert.Single(step.Questions));
        Assert.Equal(next, followUp.Id);
        Assert.Empty(step.Placements);
        Assert.Equal([next], (await clusterer.GetOpenQuestionsAsync()).Select(q => q.Id));
    }

    [Fact]
    public async Task A_yes_never_puts_an_item_into_a_cluster_that_refused_it_through_a_merge()
    {
        var clusterer = new Clusterer(new InMemoryClusterStore(), Judged(o =>
        {
            o.JoinSimilarity = 0.97;
            o.MergeSimilarity = o.MergeCheckSimilarity = 0.95;
        }));
        await clusterer.AddAsync([Item("a", At(0))]);
        var b = (await clusterer.AddAsync([Item("b", At(15))])).Questions[0];
        await clusterer.AnswerAsync([Verdict.Unanswerable(b.Id)]);   // b starts cluster 2
        var toFirst = Assert.IsType<JoinQuestion>((await clusterer.AddAsync([Item("x", At(-35))])).Questions[0]);
        Assert.Equal(1, toFirst.ClusterId);
        var toSecond = Assert.IsType<JoinQuestion>(Assert.Single((await clusterer.AnswerAsync([Verdict.Different(toFirst.Id)])).Questions));
        Assert.Equal(2, toSecond.ClusterId);

        Assert.Equal(new ClusterMerge(2, 1, PlacementReason.Similarity), Assert.Single((await clusterer.MaintainAsync()).Merges));
        var step = await clusterer.AnswerAsync([Verdict.Same(toSecond.Id)]);

        Assert.Equal([new Placement("x", 3, PlacementReason.NewCluster)], step.Placements);
    }

    [Fact]
    public async Task Removing_an_asking_item_lets_a_waiting_one_ask()
    {
        var clusterer = new Clusterer(new InMemoryClusterStore(), Judged(o => o.MaxOpenQuestions = 1));
        await clusterer.AddAsync([Item("a", At(0))]);
        await clusterer.AddAsync([Item("b", At(AngleFor(0.75))), Item("w", At(-AngleFor(0.75)))]);
        Assert.Equal(1, (await clusterer.Store.GetInfoAsync(default)).WaitingItems);

        await clusterer.RemoveAsync(["b"]);

        var question = Assert.IsType<JoinQuestion>(Assert.Single(await clusterer.GetOpenQuestionsAsync()));
        Assert.Equal("w", question.ItemId);
        Assert.Equal(0, (await clusterer.Store.GetInfoAsync(default)).WaitingItems);
    }

    [Fact]
    public async Task Removing_a_waiting_item_takes_it_off_the_queue()
    {
        var clusterer = new Clusterer(new InMemoryClusterStore(), Judged(o => o.MaxOpenQuestions = 1));
        await clusterer.AddAsync([Item("a", At(0))]);
        var asked = (await clusterer.AddAsync([Item("b", At(AngleFor(0.75))), Item("w", At(-AngleFor(0.75)))])).Questions[0];

        await clusterer.RemoveAsync(["w"]);
        var step = await clusterer.AnswerAsync([Verdict.Same(asked.Id)]);

        Assert.Equal(0, (await clusterer.Store.GetInfoAsync(default)).WaitingItems);
        Assert.Empty(step.Questions);
        Assert.Null(await clusterer.GetClusterOfAsync("w"));
    }

    [Fact]
    public async Task A_huge_question_limit_drains_waiting_items_without_overflowing()
    {
        var store = new InMemoryClusterStore();
        var tight = new Clusterer(store, Judged(o => o.MaxOpenQuestions = 1));
        await tight.AddAsync([Item("a", At(0))]);
        await tight.AddAsync([Item("b", At(AngleFor(0.75))), Item("w", At(-AngleFor(0.75)))]);

        var open = Assert.Single(await tight.GetOpenQuestionsAsync());

        // An answer loads an item into the call, which used to push the drain's batch size past int.MaxValue.
        var loose = new Clusterer(store, Judged(o => o.MaxOpenQuestions = int.MaxValue));
        var step = await loose.AnswerAsync([Verdict.Different(open.Id)]);   // b starts its own cluster; w may ask cluster 1

        Assert.Equal("w", Assert.IsType<JoinQuestion>(Assert.Single(step.Questions)).ItemId);
    }

    [Fact]
    public async Task A_split_is_not_merged_back_on_the_next_maintenance()
    {
        var clusterer = new Clusterer(new InMemoryClusterStore(), CosineOnly(o =>
        {
            o.JoinSimilarity = o.CheckSimilarity = o.MergeSimilarity = o.MergeCheckSimilarity = 0.5;
            o.Split.Enabled = true;
            o.Split.CohesionThreshold = 0.95;
            o.Split.RegroupSimilarity = 0.95;
            o.Split.MinPartSize = 2;
        }));
        await clusterer.AddAsync([Item("a", At(0)), Item("b", At(2)), Item("c", At(50)), Item("d", At(52))]);

        Assert.Single((await clusterer.MaintainAsync()).Splits);
        var second = await clusterer.MaintainAsync();
        var third = await clusterer.MaintainAsync();

        Assert.Empty(second.Merges);
        Assert.Empty(second.Splits);
        Assert.Equal(ClusteringStep.Empty.Merges, third.Merges);
        Assert.Equal(2, await clusterer.ListClustersAsync().CountAsync());
    }

    [Fact]
    public async Task A_merge_question_that_never_got_an_answer_is_asked_again_after_a_change()
    {
        var clusterer = new Clusterer(new InMemoryClusterStore(), Judged(o =>
        {
            o.CheckSimilarity = o.JoinSimilarity;
            o.FailedAnswers.MaxAttempts = 1;
        }));
        await clusterer.AddAsync([Item("a", At(0)), Item("b", At(AngleFor(0.85)))]);
        var question = (await clusterer.MaintainAsync()).Questions[0];
        await clusterer.AnswerAsync([Verdict.Failed(question.Id)]);
        Assert.Empty((await clusterer.MaintainAsync()).Questions);

        await clusterer.AddAsync([Item("a2", At(1))]);   // cluster 1 changes
        var again = await clusterer.MaintainAsync();

        Assert.IsType<MergeQuestion>(Assert.Single(again.Questions));
    }

    [Fact]
    public async Task An_unanswerable_merge_question_settles_the_pair()
    {
        var clusterer = new Clusterer(new InMemoryClusterStore(), Judged(o => o.CheckSimilarity = o.JoinSimilarity));
        await clusterer.AddAsync([Item("a", At(0)), Item("b", At(AngleFor(0.85)))]);
        var question = (await clusterer.MaintainAsync()).Questions[0];

        Assert.Empty((await clusterer.AnswerAsync([Verdict.Unanswerable(question.Id)])).Merges);
        await clusterer.AddAsync([Item("a2", At(1))]);   // cluster 1 changes

        Assert.Empty((await clusterer.MaintainAsync()).Questions);
    }

    [Fact]
    public async Task A_judged_no_on_a_merge_holds_after_a_change()
    {
        var clusterer = new Clusterer(new InMemoryClusterStore(), Judged(o => o.CheckSimilarity = o.JoinSimilarity));
        await clusterer.AddAsync([Item("a", At(0)), Item("b", At(AngleFor(0.85)))]);
        var question = (await clusterer.MaintainAsync()).Questions[0];

        await clusterer.AnswerAsync([Verdict.Different(question.Id)]);
        await clusterer.AddAsync([Item("a2", At(1))]);

        Assert.Empty((await clusterer.MaintainAsync()).Questions);
    }

    [Fact]
    public async Task An_empty_cluster_is_never_asked_about()
    {
        var clusterer = new Clusterer(new InMemoryClusterStore(), Judged());
        await clusterer.AddAsync([Item("a", At(0)), Item("far", At(150))]);
        await clusterer.RemoveAsync(["a"]);

        var step = await clusterer.AddAsync([Item("b", At(AngleFor(0.75)))]);

        Assert.Empty(step.Questions);
        Assert.Equal([new Placement("b", 3, PlacementReason.NewCluster)], step.Placements);
        Assert.Empty((await clusterer.MaintainAsync()).Questions);
    }

    [Fact]
    public async Task With_no_checks_allowed_an_item_never_asks()
    {
        var clusterer = new Clusterer(new InMemoryClusterStore(), Judged(o => o.MaxChecksPerItem = 0));
        await clusterer.AddAsync([Item("a", At(0))]);

        var step = await clusterer.AddAsync([Item("b", At(AngleFor(0.75)))]);

        Assert.Empty(step.Questions);
        Assert.Equal([new Placement("b", 2, PlacementReason.NewCluster)], step.Placements);
    }

    [Fact]
    public async Task A_chain_of_merges_redirects_every_old_id_to_the_survivor()
    {
        var clusterer = new Clusterer(new InMemoryClusterStore(), CosineOnly(o => o.MergeSimilarity = o.MergeCheckSimilarity = 0.8));
        await clusterer.AddAsync([Item("a", At(0)), Item("a2", At(1)), Item("b", At(30))]);
        await clusterer.MaintainAsync();                       // 2 merges into 1
        await clusterer.AddAsync([Item("c", At(-22))]);         // cluster 3: 0.85 from cluster 1
        await clusterer.MaintainAsync();                       // 3 merges into 1

        Assert.Equal(1, (await clusterer.GetClusterAsync(2))!.Id);
        Assert.Equal(1, (await clusterer.GetClusterAsync(3))!.Id);
        Assert.Equal(["a", "a2", "b", "c"], await clusterer.GetMembersAsync(3));
    }

    [Fact]
    public async Task One_maintenance_call_checks_at_most_the_configured_number_of_clusters()
    {
        var store = new InMemoryClusterStore();
        var clusterer = new Clusterer(store, CosineOnly(o => o.MaxClustersPerMaintain = 1));
        await clusterer.AddAsync([Item("a", At(0)), Item("b", At(60)), Item("c", At(120))]);

        await clusterer.MaintainAsync();

        Assert.Equal([2L, 3L], (await store.GetChangedClustersAsync(0, 10, default)).Select(c => c.Id));
    }

    [Fact]
    public async Task Clusters_held_back_for_lack_of_question_room_do_not_starve_the_others()
    {
        var clusterer = new Clusterer(new InMemoryClusterStore(), Judged(o =>
        {
            o.MergeSimilarity = 0.85;
            o.MergeCheckSimilarity = 0.8;
            o.MaxOpenQuestions = 1;
            o.MaxClustersPerMaintain = 2;
        }));
        await clusterer.AddAsync([Item("a", At(0))]);
        await clusterer.AnswerAsync([Verdict.Unanswerable((await clusterer.AddAsync([Item("b", At(32))])).Questions[0].Id)]);     // cluster 2: 0.85 from 1, a close call
        await clusterer.AddAsync([Item("c", At(180))]);
        await clusterer.AnswerAsync([Verdict.Unanswerable((await clusterer.AddAsync([Item("d", At(208))])).Questions[0].Id)]);    // cluster 4: 0.88 from 3, certain
        Assert.Single((await clusterer.AddAsync([Item("x", At(-45.6))])).Questions);                                               // the one question room is taken

        var first = await clusterer.MaintainAsync();    // clusters 1 and 2: their question must wait
        var second = await clusterer.MaintainAsync();   // the next call moves on to 3 and 4

        Assert.Empty(first.Merges);
        Assert.Equal([new ClusterMerge(4, 3, PlacementReason.Similarity)], second.Merges);
    }

    [Fact]
    public async Task A_refusing_cluster_that_merges_into_the_one_asked_still_keeps_the_item_out()
    {
        var clusterer = new Clusterer(new InMemoryClusterStore(), Judged(o =>
        {
            o.JoinSimilarity = 0.97;
            o.MergeSimilarity = o.MergeCheckSimilarity = 0.95;
        }));
        await clusterer.AddAsync([Item("a", At(0))]);
        await clusterer.AnswerAsync([Verdict.Unanswerable((await clusterer.AddAsync([Item("b", At(15))])).Questions[0].Id)]);
        await clusterer.AddAsync([Item("b2", At(16))]);                                   // cluster 2 has two members
        var toFirst = Assert.IsType<JoinQuestion>((await clusterer.AddAsync([Item("x", At(-35))])).Questions[0]);
        Assert.Equal(1, toFirst.ClusterId);
        var toSecond = Assert.IsType<JoinQuestion>(Assert.Single((await clusterer.AnswerAsync([Verdict.Different(toFirst.Id)])).Questions));
        Assert.Equal(2, toSecond.ClusterId);

        Assert.Equal(new ClusterMerge(1, 2, PlacementReason.Similarity), Assert.Single((await clusterer.MaintainAsync()).Merges));
        var step = await clusterer.AnswerAsync([Verdict.Same(toSecond.Id)]);

        Assert.Equal([new Placement("x", 3, PlacementReason.NewCluster)], step.Placements);
    }

    [Fact]
    public async Task A_cluster_merged_into_is_not_split_in_the_same_call()
    {
        var clusterer = new Clusterer(new InMemoryClusterStore(), CosineOnly(o =>
        {
            o.MergeSimilarity = o.MergeCheckSimilarity = 0.6;
            o.Split.Enabled = true;
            o.Split.CohesionThreshold = 0.95;
            o.Split.RegroupSimilarity = 0.95;
            o.Split.MinPartSize = 2;
        }));
        await clusterer.AddAsync([Item("a", At(0)), Item("b", At(2)), Item("c", At(50)), Item("d", At(52))]);

        var step = await clusterer.MaintainAsync();

        Assert.Single(step.Merges);
        Assert.Empty(step.Splits);
    }

    [Fact]
    public async Task Undefined_answers_and_null_ids_are_refused()
    {
        var clusterer = new Clusterer(new InMemoryClusterStore(), Judged());

        await Assert.ThrowsAsync<ArgumentException>(() => clusterer.AnswerAsync([new Verdict(new QuestionId(1), (VerdictKind)9)]));
        await Assert.ThrowsAsync<ArgumentException>(() => clusterer.RemoveAsync([null!]));
    }

    [Theory]
    [InlineData("""{"SchemaVersion":1,"Info":null,"Clusters":[],"Items":[],"Questions":[],"MergeChecks":[]}""")]
    [InlineData("""{"SchemaVersion":1}""")]
    [InlineData("""{"SchemaVersion":1,"Info":{"Version":1,"EmbeddingModel":"m","Dimensions":2,"NextClusterId":2,"NextQuestionId":1,"NextItemSequence":3,"OpenQuestions":0,"WaitingItems":0},"Clusters":[{"Id":1,"Centroid":[1,0],"Sum":[2,0],"Count":2,"Label":null,"MergedIntoId":null,"Changed":true,"RemovalsSinceRecompute":0,"RefusedBy":[]}],"Items":[{"Id":"a","Sequence":1,"Embedding":[1,0],"ClusterId":1,"State":"Placed","RefusedClusterIds":[],"OpenQuestionId":null,"JudgeUnavailable":false},{"Id":"a","Sequence":2,"Embedding":[1,0],"ClusterId":1,"State":"Placed","RefusedClusterIds":[],"OpenQuestionId":null,"JudgeUnavailable":false}],"Questions":[],"MergeChecks":[]}""")]
    [InlineData("""{"SchemaVersion":1,"Info":{"Version":1,"EmbeddingModel":"m","Dimensions":2,"NextClusterId":1,"NextQuestionId":1,"NextItemSequence":1,"OpenQuestions":3,"WaitingItems":0},"Clusters":[],"Items":[],"Questions":[],"MergeChecks":[]}""")]
    [InlineData("""{"SchemaVersion":1,"Info":{"Version":1,"EmbeddingModel":"m","Dimensions":2,"NextClusterId":2,"NextQuestionId":1,"NextItemSequence":2,"OpenQuestions":0,"WaitingItems":0},"Clusters":[{"Id":1,"Centroid":[1,0],"Sum":[1,0],"Count":5,"Label":null,"MergedIntoId":null,"Changed":true,"RemovalsSinceRecompute":0,"RefusedBy":[]}],"Items":[{"Id":"a","Sequence":1,"Embedding":[1,0],"ClusterId":1,"State":"Placed","RefusedClusterIds":[],"OpenQuestionId":null,"JudgeUnavailable":false}],"Questions":[],"MergeChecks":[]}""")]
    [InlineData("""{"SchemaVersion":1,"Info":{"Version":1,"EmbeddingModel":"m","Dimensions":2,"NextClusterId":3,"NextQuestionId":1,"NextItemSequence":1,"OpenQuestions":0,"WaitingItems":0},"Clusters":[{"Id":1,"Centroid":[1,0],"Sum":[0,0],"Count":0,"Label":null,"MergedIntoId":2,"Changed":false,"RemovalsSinceRecompute":0,"RefusedBy":[]},{"Id":2,"Centroid":[1,0],"Sum":[0,0],"Count":0,"Label":null,"MergedIntoId":1,"Changed":false,"RemovalsSinceRecompute":0,"RefusedBy":[]}],"Items":[],"Questions":[],"MergeChecks":[]}""")]
    [InlineData("""{"SchemaVersion":1,"Info":{"Version":1,"EmbeddingModel":"m","Dimensions":2,"NextClusterId":1,"NextQuestionId":2,"NextItemSequence":1,"OpenQuestions":0,"WaitingItems":0},"Clusters":[],"Items":[],"Questions":[{"Question":{"Id":{"Value":1},"Similarity":0.7},"State":"Closed","FailedAttempts":0,"Outcome":null}],"MergeChecks":[]}""")]
    public async Task A_snapshot_that_contradicts_itself_is_refused(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        await Assert.ThrowsAsync<InvalidDataException>(() => InMemoryClusterStore.ImportAsync(stream));
    }

    [Fact]
    public async Task Nearest_clusters_from_the_bounded_search_match_a_full_sort()
    {
        var store = new InMemoryClusterStore();
        var clusterer = new Clusterer(store, CosineOnly(o => o.JoinSimilarity = o.CheckSimilarity = o.MergeSimilarity = o.MergeCheckSimilarity = 0.9999));
        await clusterer.AddAsync([.. Enumerable.Range(0, 60).Select(i => Item($"i{i}", At(i * 6 % 360)))]);
        var count = await store.ListClustersAsync(default).CountAsync();

        foreach (var degrees in new[] { 0.0, 7.5, 123.0, 359.0 })
        {
            var everything = await store.NearestClustersAsync(At(degrees), count, default);
            var nearest = await store.NearestClustersAsync(At(degrees), 7, default);
            Assert.Equal(count, everything.Count);
            Assert.True(everything.Zip(everything.Skip(1)).All(p => p.First.Similarity > p.Second.Similarity
                || (p.First.Similarity == p.Second.Similarity && p.First.Cluster.Id < p.Second.Cluster.Id)));
            Assert.Equal(everything.Take(7).Select(n => n.Cluster.Id), nearest.Select(n => n.Cluster.Id));
        }
    }
}
