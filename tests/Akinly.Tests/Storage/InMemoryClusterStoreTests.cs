using Akinly.Storage;
using static Akinly.Tests.TestVectors;

namespace Akinly.Tests.Storage;

public sealed class InMemoryClusterStoreTests
{
    [Fact]
    public async Task An_export_imports_into_a_store_that_continues_where_the_first_left_off()
    {
        var store = new InMemoryClusterStore();
        var clusterer = new Clusterer(store, Judged());
        {
            await clusterer.AddAsync([Item("a", At(0)), Item("b", At(2))]);
            await clusterer.SetLabelAsync(1, "Heating repairs");
            await clusterer.AddAsync([Item("c", At(AngleFor(0.7)))]);
        }

        using var stream = new MemoryStream();
        await store.ExportAsync(stream);
        stream.Position = 0;
        var restored = await InMemoryClusterStore.ImportAsync(stream);

        Assert.Equal(await store.GetInfoAsync(default), await restored.GetInfoAsync(default));
        var resumed = new Clusterer(restored, Judged());
        var question = Assert.IsType<JoinQuestion>(Assert.Single(await resumed.GetOpenQuestionsAsync()));
        Assert.Equal("c", question.ItemId);
        Assert.Equal("Heating repairs", question.ClusterLabel);
        var step = await resumed.AnswerAsync([Verdict.Same(question.Id)]);
        Assert.Equal([new Placement("c", 1, PlacementReason.Judge)], step.Placements);
        Assert.Equal(["a", "b", "c"], await resumed.GetMembersAsync(1));
    }

    [Fact]
    public async Task An_unreadable_or_newer_snapshot_is_refused()
    {
        using var garbage = new MemoryStream("not json"u8.ToArray());
        await Assert.ThrowsAsync<InvalidDataException>(() => InMemoryClusterStore.ImportAsync(garbage));

        using var newer = new MemoryStream("""{"SchemaVersion":99,"Info":{"Version":0,"EmbeddingModel":null,"Dimensions":0,"NextClusterId":1,"NextQuestionId":1,"NextItemSequence":1,"OpenQuestions":0,"WaitingItems":0},"Clusters":[],"Items":[],"Questions":[],"MergeChecks":[]}"""u8.ToArray());
        await Assert.ThrowsAsync<InvalidDataException>(() => InMemoryClusterStore.ImportAsync(newer));

        using var empty = new MemoryStream("null"u8.ToArray());
        await Assert.ThrowsAsync<InvalidDataException>(() => InMemoryClusterStore.ImportAsync(empty));
    }

    [Fact]
    public async Task A_write_from_a_stale_version_is_refused()
    {
        var store = new InMemoryClusterStore();
        var change = new ChangeSet(StoreInfo.Empty with { Version = 1 }, [], [], [], [], []);
        await store.ApplyAsync(change, 0, default);

        var conflict = await Assert.ThrowsAsync<ClusterStoreConflictException>(() => store.ApplyAsync(change, 0, default));

        Assert.Equal(0, conflict.ExpectedVersion);
        Assert.Equal(1, conflict.ActualVersion);
        await Assert.ThrowsAsync<ArgumentException>(() => store.ApplyAsync(change with { Info = StoreInfo.Empty with { Version = 5 } }, 1, default));
    }

    [Fact]
    public async Task Nearest_clusters_come_most_similar_first_with_ties_by_id()
    {
        var store = new InMemoryClusterStore();
        var clusterer = new Clusterer(store, CosineOnly());
        await clusterer.AddAsync([Item("a", At(30)), Item("b", At(-30)), Item("c", At(90))]);

        var nearest = await store.NearestClustersAsync(At(0), 3, default);

        Assert.Equal([1L, 2L, 3L], nearest.Select(n => n.Cluster.Id));
        Assert.Equal(nearest[0].Similarity, nearest[1].Similarity, 6);
        Assert.Empty(await store.NearestClustersAsync(At(0), 0, default));
    }

    [Fact]
    public async Task Examples_are_the_members_nearest_the_centroid()
    {
        var store = new InMemoryClusterStore();
        var clusterer = new Clusterer(store, CosineOnly(o => o.JoinSimilarity = o.CheckSimilarity = 0.5));
        await clusterer.AddAsync([Item("far", At(-30)), Item("mid", At(0)), Item("near", At(5)), Item("other", At(30))]);

        var examples = await store.GetExamplesAsync(1, 2, default);

        Assert.Equal(2, examples.Count);
        Assert.DoesNotContain("far", examples);
        Assert.Empty(await store.GetExamplesAsync(404, 2, default));
    }

    [Fact]
    public async Task Lookups_skip_unknown_ids_and_list_only_live_clusters()
    {
        var store = new InMemoryClusterStore();
        var clusterer = new Clusterer(store, CosineOnly(o => o.MergeSimilarity = o.MergeCheckSimilarity = 0.8));
        await clusterer.AddAsync([Item("a", At(0)), Item("b", At(30))]);
        Assert.Single((await clusterer.MaintainAsync()).Merges);
        Assert.Equal([1L], (await store.GetChangedClustersAsync(0, 100, default)).Select(c => c.Id));   // merged into: checked next time
        await clusterer.MaintainAsync();

        Assert.Equal([1L, 2L], (await store.GetClustersAsync([1, 2, 2, 404], default)).Select(c => c.Id));
        Assert.Equal([1L], await store.ListClustersAsync(default).Select(c => c.Id).ToListAsync());
        Assert.Equal(["a"], (await store.GetItemsAsync(["a", "zzz"], default)).Select(i => i.Id));
        Assert.Empty(await store.GetQuestionsAsync([new QuestionId(9)], default));
        Assert.Empty(await store.GetMergeChecksAsync([(1, 2)], default));
        Assert.Empty(await store.GetChangedClustersAsync(0, 100, default));
    }
}
