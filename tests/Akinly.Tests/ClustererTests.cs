using Akinly.Storage;
using static Akinly.Tests.TestVectors;

namespace Akinly.Tests;

public sealed class ClustererTests
{
    private static Clusterer Create(ClusteringOptions options, IClusterStore? store = null) =>
        new(store ?? new InMemoryClusterStore(), options);

    [Fact]
    public async Task Close_items_join_one_cluster_and_far_ones_start_their_own()
    {
        var clusterer = Create(CosineOnly());

        var step = await clusterer.AddAsync([Item("a", At(0)), Item("b", At(10)), Item("c", At(90))]);

        Assert.Equal(
            [new Placement("a", 1, PlacementReason.NewCluster), new Placement("b", 1, PlacementReason.Similarity), new Placement("c", 2, PlacementReason.NewCluster)],
            step.Placements);
        Assert.Empty(step.Questions);
        var clusters = await clusterer.ListClustersAsync().ToListAsync();
        Assert.Equal([2, 1], clusters.Select(c => c.Count));
        Assert.Equal(["a", "b"], await clusterer.GetMembersAsync(1));
        Assert.Equal(2L, await clusterer.GetClusterOfAsync("c"));
    }

    [Fact]
    public async Task The_centroid_is_the_exact_mean_direction_of_the_members()
    {
        var clusterer = Create(CosineOnly(o => o.JoinSimilarity = o.CheckSimilarity = 0.5));

        await clusterer.AddAsync([Item("a", At(0)), Item("b", At(20)), Item("c", At(40))]);

        var cluster = Assert.Single(await clusterer.ListClustersAsync().ToListAsync());
        Assert.Equal(3, cluster.Count);
        Assert.Equal(1.0, Vectors.Dot(cluster.Centroid.ToArray(), At(20)), 6);
    }

    [Fact]
    public async Task A_close_call_becomes_a_question_with_examples_and_waits_for_an_answer()
    {
        var clusterer = Create(Judged());
        await clusterer.AddAsync([Item("a", At(0)), Item("b", At(5))]);

        var step = await clusterer.AddAsync([Item("c", At(AngleFor(0.75)))]);

        Assert.Empty(step.Placements);
        var question = Assert.IsType<JoinQuestion>(Assert.Single(step.Questions));
        Assert.Equal("c", question.ItemId);
        Assert.Equal(1, question.ClusterId);
        Assert.Equal(Math.Cos((AngleFor(0.75) - 2.5) * Math.PI / 180), question.Similarity, 4);
        Assert.Equal(["a", "b"], question.ExampleItemIds.Order());
        Assert.Null(await clusterer.GetClusterOfAsync("c"));
        Assert.Equal([question], await clusterer.GetOpenQuestionsAsync());
    }

    [Fact]
    public async Task A_yes_joins_the_cluster_however_far_it_is()
    {
        var clusterer = Create(Judged());
        await clusterer.AddAsync([Item("a", At(0))]);
        var question = (await clusterer.AddAsync([Item("b", At(AngleFor(0.7)))])).Questions[0];

        var step = await clusterer.AnswerAsync([Verdict.Same(question.Id)]);

        Assert.Equal([new Placement("b", 1, PlacementReason.Judge)], step.Placements);
        Assert.Empty(await clusterer.GetOpenQuestionsAsync());
        Assert.Equal(2, (await clusterer.GetClusterAsync(1))!.Count);
    }

    [Fact]
    public async Task A_no_tries_the_next_nearest_cluster_then_starts_a_new_one()
    {
        var clusterer = Create(Judged(o => o.MaxChecksPerItem = 2));
        await clusterer.AddAsync([Item("left", At(-40)), Item("right", At(40))]);

        var first = Assert.IsType<JoinQuestion>(Assert.Single((await clusterer.AddAsync([Item("x", At(-5))])).Questions));
        Assert.Equal(1, first.ClusterId);

        var second = Assert.IsType<JoinQuestion>(Assert.Single((await clusterer.AnswerAsync([Verdict.Different(first.Id)])).Questions));
        Assert.Equal(2, second.ClusterId);

        var step = await clusterer.AnswerAsync([Verdict.Different(second.Id)]);
        Assert.Equal([new Placement("x", 3, PlacementReason.NewCluster)], step.Placements);
    }

    [Fact]
    public async Task A_failure_is_asked_again_and_after_the_last_attempt_similarity_decides()
    {
        var clusterer = Create(Judged(o => o.FailedAnswers.MaxAttempts = 2));
        await clusterer.AddAsync([Item("a", At(0))]);
        var question = (await clusterer.AddAsync([Item("b", At(AngleFor(0.7)))])).Questions[0];

        var afterFirst = await clusterer.AnswerAsync([Verdict.Failed(question.Id)]);
        Assert.Empty(afterFirst.Placements);
        Assert.Equal([question.Id], (await clusterer.GetOpenQuestionsAsync()).Select(q => q.Id));

        var afterSecond = await clusterer.AnswerAsync([Verdict.Failed(question.Id)]);

        // Below the join threshold and never refused: by similarity alone it starts its own.
        Assert.Equal([new Placement("b", 2, PlacementReason.NewCluster)], afterSecond.Placements);
        Assert.Empty(await clusterer.GetOpenQuestionsAsync());
    }

    [Fact]
    public async Task An_unanswerable_question_closes_at_once_and_similarity_decides()
    {
        var clusterer = Create(Judged());
        await clusterer.AddAsync([Item("a", At(0))]);
        var question = (await clusterer.AddAsync([Item("b", At(AngleFor(0.7)))])).Questions[0];

        var step = await clusterer.AnswerAsync([Verdict.Unanswerable(question.Id)]);

        Assert.Equal([new Placement("b", 2, PlacementReason.NewCluster)], step.Placements);
    }

    [Fact]
    public async Task Answers_to_unknown_or_closed_questions_are_ignored_and_the_first_answer_counts()
    {
        var clusterer = Create(Judged());
        await clusterer.AddAsync([Item("a", At(0))]);
        var question = (await clusterer.AddAsync([Item("b", At(AngleFor(0.7)))])).Questions[0];

        var step = await clusterer.AnswerAsync([Verdict.Same(question.Id), Verdict.Different(question.Id), Verdict.Same(new QuestionId(999))]);
        var again = await clusterer.AnswerAsync([Verdict.Different(question.Id)]);

        Assert.Equal([new Placement("b", 1, PlacementReason.Judge)], step.Placements);
        Assert.Equal(ClusteringStep.Empty.Placements, again.Placements);
        Assert.Equal(1L, await clusterer.GetClusterOfAsync("b"));
    }

    [Fact]
    public async Task Items_wait_while_the_open_questions_are_at_the_limit_and_ask_when_room_frees()
    {
        var clusterer = Create(Judged(o => o.MaxOpenQuestions = 1));
        await clusterer.AddAsync([Item("a", At(0))]);

        var step = await clusterer.AddAsync([Item("b", At(AngleFor(0.7))), Item("c", At(-AngleFor(0.7)))]);
        var first = Assert.Single(step.Questions);
        Assert.Equal(1, (await clusterer.Store.GetInfoAsync(default)).WaitingItems);

        // b is refused and starts its own cluster, which frees the one open question for c.
        var next = await clusterer.AnswerAsync([Verdict.Different(first.Id)]);

        var second = Assert.IsType<JoinQuestion>(Assert.Single(next.Questions));
        Assert.Equal("c", second.ItemId);
        Assert.Equal(0, (await clusterer.Store.GetInfoAsync(default)).WaitingItems);
    }

    [Fact]
    public async Task Close_clusters_merge_into_the_larger_and_the_smaller_id_redirects()
    {
        var clusterer = Create(CosineOnly(o => o.MergeSimilarity = o.MergeCheckSimilarity = 0.8));
        await clusterer.AddAsync([Item("a", At(0)), Item("b", At(1)), Item("c", At(30))]);

        var step = await clusterer.MaintainAsync();

        Assert.Equal([new ClusterMerge(2, 1, PlacementReason.Similarity)], step.Merges);
        Assert.Equal(1, (await clusterer.GetClusterAsync(2))!.Id);
        Assert.Equal(["a", "b", "c"], await clusterer.GetMembersAsync(2));
        Assert.Equal(1L, await clusterer.GetClusterOfAsync("c"));
        Assert.Equal(3, Assert.Single(await clusterer.ListClustersAsync().ToListAsync()).Count);
    }

    [Fact]
    public async Task A_close_pair_of_clusters_is_asked_about_once_and_a_yes_merges_them()
    {
        var clusterer = Create(Judged(o => o.CheckSimilarity = o.JoinSimilarity));
        await clusterer.AddAsync([Item("a", At(0)), Item("b", At(AngleFor(0.85)))]);

        var asked = await clusterer.MaintainAsync();
        var question = Assert.IsType<MergeQuestion>(Assert.Single(asked.Questions));
        Assert.Equal((1, 2), (question.ClusterId, question.OtherClusterId));
        Assert.Equal(["a"], question.ExampleItemIds);
        Assert.Equal(["b"], question.OtherExampleItemIds);
        Assert.Empty((await clusterer.MaintainAsync()).Questions);

        var merged = await clusterer.AnswerAsync([Verdict.Same(question.Id)]);

        Assert.Equal([new ClusterMerge(2, 1, PlacementReason.Judge)], merged.Merges);
    }

    [Fact]
    public async Task A_late_yes_is_ignored_when_the_clusters_have_drifted_apart()
    {
        var clusterer = Create(Judged(o => o.CheckSimilarity = o.JoinSimilarity));
        await clusterer.AddAsync([Item("a", At(0)), Item("b", At(AngleFor(0.85)))]);
        var question = (await clusterer.MaintainAsync()).Questions[0];

        // Cluster 2 grows away from cluster 1 before the answer arrives.
        await clusterer.AddAsync([Item("c", At(AngleFor(0.85) + 20)), Item("d", At(AngleFor(0.85) + 25))]);
        var cluster2 = await clusterer.GetClusterAsync(2);
        Assert.True(Vectors.Dot(cluster2!.Centroid.ToArray(), At(0)) < 0.8);

        var step = await clusterer.AnswerAsync([Verdict.Same(question.Id)]);

        Assert.Empty(step.Merges);
    }

    /// <summary>
    /// Two clusters of two members each, built by a judge: every member shares one direction and
    /// has one of its own, so any two members are 0.5 alike, the clusters' centroids 0.67, and the
    /// average similarity of a member of one to a member of the other 0.5. Merges happen at 0.6,
    /// with no question.
    /// </summary>
    private static async Task<(Clusterer Clusterer, ClusteringStep Step)> MixedClustersAsync(ClusterLinkage linkage)
    {
        static float[] Own(int axis)
        {
            var v = new float[5];
            v[axis] = 1;
            v[4] = 1;
            return v;
        }

        var clusterer = Create(Judged(o =>
        {
            o.CheckSimilarity = 0.4;
            o.MergeSimilarity = 0.6;
            o.MergeCheckSimilarity = 0.6;
            o.MergeRefusals = RefusalRule.Ignore;
            o.MergeLinkage = linkage;
        }));
        var judge = new DelegateJudge(q => q is JoinQuestion j && (j.ItemId, j.ClusterId) is ("q", 1) or ("t", 2) ? VerdictKind.Same : VerdictKind.Different);
        var step = await clusterer.SettleAsync([Item("p", Own(0)), Item("q", Own(1)), Item("r", Own(2)), Item("t", Own(3))], judge);
        Assert.Equal((1L, 1L, 2L, 2L), (await clusterer.GetClusterOfAsync("p"), await clusterer.GetClusterOfAsync("q"), await clusterer.GetClusterOfAsync("r"), await clusterer.GetClusterOfAsync("t")));
        return (clusterer, step.Then(await clusterer.MaintainAsync()));
    }

    [Fact]
    public async Task By_average_similarity_mixed_clusters_with_alike_centroids_stay_apart()
    {
        var (_, step) = await MixedClustersAsync(ClusterLinkage.Average);

        Assert.Empty(step.Merges);
    }

    [Fact]
    public async Task By_centroid_similarity_mixed_clusters_with_alike_centroids_merge()
    {
        var (_, step) = await MixedClustersAsync(ClusterLinkage.Centroid);

        Assert.Equal(new ClusterMerge(2, 1, PlacementReason.Similarity), Assert.Single(step.Merges));
    }

    [Fact]
    public async Task A_merge_question_carries_the_average_similarity_of_the_members()
    {
        var clusterer = Create(Judged(o => (o.CheckSimilarity, o.MergeCheckSimilarity) = (0.9, 0.4)));
        await clusterer.AddAsync([Item("a", At(0)), Item("b", At(2)), Item("c", At(40)), Item("d", At(42))]);

        var question = Assert.IsType<MergeQuestion>(Assert.Single((await clusterer.MaintainAsync()).Questions));

        var expected = (Math.Cos(40 * Math.PI / 180) + Math.Cos(42 * Math.PI / 180) + Math.Cos(38 * Math.PI / 180) + Math.Cos(40 * Math.PI / 180)) / 4;
        Assert.Equal(expected, question.Similarity, 5);
    }

    /// <summary>
    /// Cluster 1 refuses b, which starts cluster 2; then cluster 1 grows toward cluster 2 until
    /// the pair is close enough to ask about.
    /// </summary>
    private static async Task<Clusterer> RefusedPairAsync(RefusalRule refusals, Action<ClusteringOptions>? adjust = null)
    {
        var clusterer = Create(Judged(o =>
        {
            o.MaxChecksPerItem = 1;
            o.MergeRefusals = refusals;
            adjust?.Invoke(o);
        }));
        await clusterer.AddAsync([Item("a", At(0))]);
        var question = (await clusterer.AddAsync([Item("b", At(AngleFor(0.75)))])).Questions[0];
        await clusterer.AnswerAsync([Verdict.Different(question.Id)]);
        Assert.Equal(2L, await clusterer.GetClusterOfAsync("b"));
        await clusterer.AddAsync([Item("c", At(20)), Item("d", At(21))]);
        Assert.Equal(1L, await clusterer.GetClusterOfAsync("d"));
        return clusterer;
    }

    [Fact]
    public async Task A_merge_is_skipped_when_one_cluster_refused_a_member_of_the_other()
    {
        var clusterer = await RefusedPairAsync(RefusalRule.Block);

        var step = await clusterer.MaintainAsync();

        Assert.Empty(step.Merges);
        Assert.Empty(step.Questions);
    }

    [Theory]
    [InlineData(RefusalRule.Ask)]
    [InlineData(RefusalRule.Ignore)]
    public async Task Unless_refusals_block_the_same_pair_is_asked_about(RefusalRule refusals)
    {
        var clusterer = await RefusedPairAsync(refusals);

        var step = await clusterer.MaintainAsync();

        var question = Assert.IsType<MergeQuestion>(Assert.Single(step.Questions));
        Assert.Equal((1, 2), (question.ClusterId, question.OtherClusterId));
    }

    [Fact]
    public async Task When_refusals_ask_a_judge_a_yes_merges_the_refused_pair()
    {
        var clusterer = await RefusedPairAsync(RefusalRule.Ask);
        var question = Assert.Single((await clusterer.MaintainAsync()).Questions);

        var step = await clusterer.AnswerAsync([Verdict.Same(question.Id)]);

        Assert.Equal(new ClusterMerge(2, 1, PlacementReason.Judge), Assert.Single(step.Merges));
    }

    [Fact]
    public async Task When_refusals_block_a_late_yes_does_not_merge_the_refused_pair()
    {
        // Asked while refusals were ignored, answered after the rule changed to Block.
        var store = new InMemoryClusterStore();
        var first = new Clusterer(store, Judged(o =>
        {
            o.MaxChecksPerItem = 1;
            o.MergeRefusals = RefusalRule.Ignore;
        }));
        await first.AddAsync([Item("a", At(0))]);
        var refusal = (await first.AddAsync([Item("b", At(AngleFor(0.75)))])).Questions[0];
        await first.AnswerAsync([Verdict.Different(refusal.Id)]);
        await first.AddAsync([Item("c", At(20)), Item("d", At(21))]);
        var question = Assert.Single((await first.MaintainAsync()).Questions);
        var blocking = new Clusterer(store, Judged(o =>
        {
            o.MaxChecksPerItem = 1;
            o.MergeRefusals = RefusalRule.Block;
        }));

        var step = await blocking.AnswerAsync([Verdict.Same(question.Id)]);

        Assert.Empty(step.Merges);
    }

    [Fact]
    public async Task When_refusals_ask_a_judge_a_refused_pair_above_the_merge_threshold_is_asked_not_merged()
    {
        // The merge threshold below the pair's similarity: without the refusal it would merge outright.
        var clusterer = await RefusedPairAsync(RefusalRule.Ask, o => (o.MergeSimilarity, o.MergeCheckSimilarity) = (0.81, 0.6));

        var step = await clusterer.MaintainAsync();

        Assert.Empty(step.Merges);
        Assert.IsType<MergeQuestion>(Assert.Single(step.Questions));
    }

    [Fact]
    public async Task With_no_merge_ask_zone_a_refusal_that_asks_a_judge_keeps_the_pair_apart()
    {
        var clusterer = await RefusedPairAsync(RefusalRule.Ask, o => (o.MergeSimilarity, o.MergeCheckSimilarity) = (0.81, 0.81));

        var step = await clusterer.MaintainAsync();

        Assert.Empty(step.Merges);
        Assert.Empty(step.Questions);
    }

    [Fact]
    public async Task With_refusals_ignored_a_refused_pair_above_the_merge_threshold_merges()
    {
        var clusterer = await RefusedPairAsync(RefusalRule.Ignore, o => (o.MergeSimilarity, o.MergeCheckSimilarity) = (0.81, 0.6));

        var step = await clusterer.MaintainAsync();

        Assert.Single(step.Merges);
    }

    [Fact]
    public async Task A_join_answered_after_its_cluster_merged_goes_to_the_cluster_it_merged_into()
    {
        var clusterer = Create(Judged());
        await clusterer.AddAsync([Item("a", At(0))]);
        var unanswerable = (await clusterer.AddAsync([Item("b", At(35))])).Questions[0];
        await clusterer.AnswerAsync([Verdict.Unanswerable(unanswerable.Id)]);   // b starts cluster 2, refused nowhere
        var join = Assert.IsType<JoinQuestion>((await clusterer.AddAsync([Item("x", At(65))])).Questions[0]);
        Assert.Equal(2, join.ClusterId);

        var mergeQuestion = Assert.IsType<MergeQuestion>(Assert.Single((await clusterer.MaintainAsync()).Questions));
        var merge = Assert.Single((await clusterer.AnswerAsync([Verdict.Same(mergeQuestion.Id)])).Merges);
        Assert.Equal(new ClusterMerge(2, 1, PlacementReason.Judge), merge);

        var step = await clusterer.AnswerAsync([Verdict.Same(join.Id)]);

        Assert.Equal([new Placement("x", 1, PlacementReason.Judge)], step.Placements);
    }

    [Fact]
    public async Task Removing_items_updates_the_centroid_and_an_empty_cluster_takes_a_returning_item()
    {
        var clusterer = Create(CosineOnly(o => o.JoinSimilarity = o.CheckSimilarity = 0.5));
        await clusterer.AddAsync([Item("a", At(0)), Item("b", At(40))]);

        await clusterer.RemoveAsync(["a", "unknown"]);
        var cluster = await clusterer.GetClusterAsync(1);
        Assert.Equal(1, cluster!.Count);
        Assert.Equal(1.0, Vectors.Dot(cluster.Centroid.ToArray(), At(40)), 5);

        await clusterer.RemoveAsync(["b"]);
        Assert.Equal(0, (await clusterer.GetClusterAsync(1))!.Count);

        var step = await clusterer.AddAsync([Item("b", At(45))]);
        Assert.Equal([new Placement("b", 1, PlacementReason.Similarity)], step.Placements);
    }

    [Fact]
    public async Task Removing_an_item_with_an_open_question_closes_the_question()
    {
        var clusterer = Create(Judged());
        await clusterer.AddAsync([Item("a", At(0))]);
        var question = (await clusterer.AddAsync([Item("b", At(AngleFor(0.7)))])).Questions[0];

        await clusterer.RemoveAsync(["b"]);

        Assert.Empty(await clusterer.GetOpenQuestionsAsync());
        Assert.Empty((await clusterer.AnswerAsync([Verdict.Same(question.Id)])).Placements);
        Assert.Equal(0, (await clusterer.Store.GetInfoAsync(default)).OpenQuestions);
    }

    [Fact]
    public async Task Labels_are_kept_follow_merges_and_reach_the_questions()
    {
        var clusterer = Create(Judged());
        await clusterer.AddAsync([Item("a", At(0))]);

        Assert.True(await clusterer.SetLabelAsync(1, "Heating repairs"));
        Assert.False(await clusterer.SetLabelAsync(42, "Nothing"));
        var question = Assert.IsType<JoinQuestion>((await clusterer.AddAsync([Item("b", At(AngleFor(0.7)))])).Questions[0]);

        Assert.Equal("Heating repairs", question.ClusterLabel);
        Assert.Equal("Heating repairs", (await clusterer.GetClusterAsync(1))!.Label);
    }

    [Fact]
    public async Task A_loose_cluster_splits_in_a_cosine_only_setup()
    {
        var options = CosineOnly(o =>
        {
            o.JoinSimilarity = o.CheckSimilarity = 0.5;
            o.MergeSimilarity = o.MergeCheckSimilarity = 0.99;
            o.Split.Enabled = true;
            o.Split.CohesionThreshold = 0.95;
            o.Split.RegroupSimilarity = 0.95;
            o.Split.MinPartSize = 2;
        });
        var clusterer = Create(options);
        await clusterer.AddAsync([Item("a", At(0)), Item("b", At(2)), Item("c", At(50)), Item("d", At(52))]);
        Assert.Single(await clusterer.ListClustersAsync().ToListAsync());

        var step = await clusterer.MaintainAsync();

        var split = Assert.Single(step.Splits);
        Assert.Equal(1, split.ClusterId);
        Assert.Equal([2L], split.NewClusterIds);
        Assert.Equal([2, 2], (await clusterer.ListClustersAsync().ToListAsync()).Select(c => c.Count));
    }

    [Fact]
    public async Task Splits_are_refused_when_a_judge_decides()
    {
        var options = Judged(o => o.Split.Enabled = true);

        var exception = Assert.Throws<ArgumentException>(() => new Clusterer(new InMemoryClusterStore(), options));

        Assert.Contains("cosine-only", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_store_from_another_embedding_model_is_refused()
    {
        var store = new InMemoryClusterStore();
        var first = Create(CosineOnly(), store);
        {
            await first.AddAsync([Item("a", At(0))]);
        }

        var other = Create(CosineOnly(o => o.EmbeddingModel = "another-model"), store);

        await Assert.ThrowsAsync<EmbeddingModelMismatchException>(() => other.AddAsync([Item("b", At(0))]));
        await Assert.ThrowsAsync<EmbeddingModelMismatchException>(() => other.MaintainAsync());
    }

    [Fact]
    public async Task Bad_items_are_refused_and_nothing_is_written()
    {
        var clusterer = Create(CosineOnly());
        await clusterer.AddAsync([Item("a", At(0))]);

        await Assert.ThrowsAsync<EmbeddingModelMismatchException>(() => clusterer.AddAsync([Item("b", [1f, 0f])]));
        await Assert.ThrowsAsync<ArgumentException>(() => clusterer.AddAsync([Item("a", At(5))]));
        await Assert.ThrowsAsync<ArgumentException>(() => clusterer.AddAsync([Item("c", At(5)), Item("c", At(6))]));
        await Assert.ThrowsAsync<ArgumentException>(() => clusterer.AddAsync([Item("", At(5))]));
        await Assert.ThrowsAsync<ArgumentException>(() => clusterer.AddAsync([Item("z", new float[4])]));
        await Assert.ThrowsAsync<ArgumentException>(() => clusterer.AddAsync([Item("n", [float.NaN, 0, 0, 0])]));
        await Assert.ThrowsAsync<ArgumentException>(() => clusterer.AddAsync([null!]));

        Assert.Equal(1, (await clusterer.Store.GetInfoAsync(default)).Version);
    }

    [Fact]
    public void Invalid_options_are_refused_with_every_problem_named()
    {
        var options = new ClusteringOptions
        {
            JoinSimilarity = 0.5,
            CheckSimilarity = 0.7,
            MaxOpenQuestions = 0,
            MergeSimilarity = 1.5,
        };

        var errors = options.Validate();

        Assert.Contains(errors, e => e.Contains("EmbeddingModel", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("CheckSimilarity must not exceed", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("MaxOpenQuestions", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("MergeSimilarity must be above 0", StringComparison.Ordinal));
        Assert.Throws<ArgumentException>(() => new Clusterer(new InMemoryClusterStore(), options));
    }

    [Fact]
    public async Task The_options_are_copied_so_later_changes_have_no_effect()
    {
        var options = CosineOnly();
        var clusterer = Create(options);
        options.JoinSimilarity = 0.1;
        options.CheckSimilarity = 0.1;

        await clusterer.AddAsync([Item("a", At(0)), Item("b", At(60))]);

        Assert.Equal(2, await clusterer.ListClustersAsync().CountAsync());
        Assert.Equal(0.9, clusterer.Options.JoinSimilarity);
    }

    [Fact]
    public async Task Two_clusterers_on_one_store_both_succeed_by_reading_again_on_a_conflict()
    {
        var store = new InMemoryClusterStore();
        var one = Create(CosineOnly(), store);
        var two = Create(CosineOnly(), store);

        await Task.WhenAll(
            Task.Run(async () =>
            {
                for (var i = 0; i < 20; i++)
                {
                    await one.AddAsync([Item($"one-{i}", At(i * 3))]);
                }
            }),
            Task.Run(async () =>
            {
                for (var i = 0; i < 20; i++)
                {
                    await two.AddAsync([Item($"two-{i}", At(180 + (i * 3)))]);
                }
            }));

        var clusters = await one.ListClustersAsync().ToListAsync();
        Assert.Equal(40, clusters.Sum(c => c.Count));
        Assert.Equal(40, (await store.GetInfoAsync(default)).Version);
    }

    [Fact]
    public async Task Cancellation_stops_a_call_before_it_writes()
    {
        var clusterer = Create(CosineOnly());
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => clusterer.AddAsync([Item("a", At(0))], cancellation.Token));

        Assert.Equal(0, (await clusterer.Store.GetInfoAsync(default)).Version);
    }
}
