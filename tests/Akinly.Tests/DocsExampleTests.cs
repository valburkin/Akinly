using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Akinly.Storage;
using Akinly.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Akinly.Tests;

// The examples pass vectors inline, as the docs write them; a real caller passes embeddings from a model.
#pragma warning disable CA1861 // Prefer 'static readonly' fields over constant array arguments

/// <summary>
/// The examples in docs/ that need only the core package, as written there (output goes to a
/// list instead of the console), so they keep compiling and doing what the guides say. Each
/// test names the guide its example comes from.
/// </summary>
public sealed class DocsExampleTests
{
    /// <summary>The getting-started clusterer after its add and maintenance: clusters 1 (three tickets) and 3 (one).</summary>
    private static async Task<Clusterer> GettingStartedClustererAsync()
    {
        var clusterer = new Clusterer(new InMemoryClusterStore(), ClusteringOptions.CosineOnly("toy-3d", joinSimilarity: 0.85));
        await clusterer.AddAsync(
        [
            new ClusterItem("ticket-1", new float[] { 1.00f, 0.00f, 0.00f }),
            new ClusterItem("ticket-2", new float[] { 0.82f, 0.57f, 0.00f }),
            new ClusterItem("ticket-3", new float[] { 0.97f, 0.26f, 0.00f }),
            new ClusterItem("ticket-4", new float[] { 0.00f, 0.00f, 1.00f }),
        ]);
        await clusterer.MaintainAsync();
        return clusterer;
    }

    // ------------------------------------------------------------ docs/getting-started.md

    [Fact]
    public async Task Getting_started_reads_the_clusters_their_members_and_the_redirect_of_a_merged_id()
    {
        var clusterer = await GettingStartedClustererAsync();
        var output = new List<string>();

        // From docs/getting-started.md, "Read the results".
        await foreach (var cluster in clusterer.ListClustersAsync())
        {
            var members = await clusterer.GetMembersAsync(cluster.Id);
            output.Add($"cluster {cluster.Id}: {cluster.Count} items: {string.Join(", ", members)}");
        }

        output.Add($"{await clusterer.GetClusterOfAsync("ticket-2")}");   // the cluster an item is in
        output.Add($"{(await clusterer.GetClusterAsync(2))?.Id}");      // a merged id leads to the cluster it merged into

        Assert.Equal(
            [
                "cluster 1: 3 items: ticket-1, ticket-2, ticket-3",
                "cluster 3: 1 items: ticket-4",
                "1",
                "1",
            ],
            output);
    }

    [Fact]
    public async Task Getting_started_saves_the_store_to_a_file_and_restores_it()
    {
        var folder = Directory.CreateTempSubdirectory("akinly-docs-");
        var path = Path.Combine(folder.FullName, "clusters.json");
        try
        {
            // From docs/getting-started.md and docs/how-to/save-and-restore.md (with a temporary path).
            var options = ClusteringOptions.CosineOnly("toy-3d", joinSimilarity: 0.85);
            var store = new InMemoryClusterStore();
            var clusterer = new Clusterer(store, options);
            await clusterer.AddAsync([new ClusterItem("ticket-1", new float[] { 1.00f, 0.00f, 0.00f })]);

            await using (var file = File.Create(path))
            {
                await store.ExportAsync(file);
            }

            // In a later run:
            InMemoryClusterStore restored;
            await using (var file = File.OpenRead(path))
            {
                restored = await InMemoryClusterStore.ImportAsync(file);
            }

            var again = new Clusterer(restored, options);
            var step = await again.AddAsync([new ClusterItem("ticket-3", new float[] { 0.97f, 0.26f, 0.00f })]);

            Assert.Equal(new Placement("ticket-3", 1, PlacementReason.Similarity), Assert.Single(step.Placements));
            Assert.Equal(["ticket-1", "ticket-3"], await again.GetMembersAsync(1));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    // ------------------------------------------------------------ docs/concepts.md (the pure functions)

    /// <summary>A stand-in embedding model: texts about heating point one way, the rest another.</summary>
    private static float[] embeddingOf(string text) =>
        text.Contains("heat", StringComparison.OrdinalIgnoreCase) ? [1f, 0.05f * text.Length / 60, 0f] : [0f, 0f, 1f];

    [Fact]
    public void The_pure_functions_put_the_two_heating_complaints_together()
    {
        // From docs/concepts.md, "Three layers".
        List<ClusterState> clusters = [];   // the clusters you already have
        long nextClusterId = 1;

        EmbeddedItem[] items =
        [
            new(1, embeddingOf("My landlord won't fix the heating")),
            new(2, embeddingOf("The heat has been out for weeks and the landlord ignores me")),
            new(3, embeddingOf("I can't find an apartment I can afford")),
        ];

        var result = IncrementalClusterer.Assign(clusters, items, joinSimilarity: 0.85);

        foreach (var updated in result.Updated) { clusters[clusters.FindIndex(c => c.Id == updated.Id)] = updated; }

        foreach (var created in result.Created)
        {
            clusters.Add(new ClusterState(nextClusterId++, created.Centroid, created.ItemIds.Count));
        }

        Assert.Equal([2, 1], clusters.Select(c => c.Count));
        Assert.Equal([[1L, 2L], [3L]], result.Created.Select(c => c.ItemIds.ToArray()));
    }

    /// <summary>A stand-in judge that answers from a fixed list of clusters it accepts, and records what it was asked.</summary>
    private sealed class FakeJudge(params long[] accepts)
    {
        public List<long> Asked { get; } = [];

        public Task<bool> SameAsync(EmbeddedItem item, long clusterId)
        {
            Asked.Add(clusterId);
            return Task.FromResult(accepts.Contains(clusterId));
        }
    }

    /// <summary>The item is about 0.82 similar to cluster 1 and 0.78 to cluster 2: both in the zone a judge decides.</summary>
    private static List<ClusterState> TwoCloseCalls() =>
    [
        new(1, Vectors.Normalize([1f, 0.7f, 0f]), 4),
        new(2, Vectors.Normalize([1f, -0.8f, 0f]), 4),
    ];

    /// <summary>The judge example in docs/concepts.md, as written there, over the given clusters and judge.</summary>
    private static async Task<AssignmentResult?> PlaceAsync(List<ClusterState> clusters, FakeJudge judge, EmbeddedItem item)
    {
        var refused = new HashSet<long>();   // clusters that refused this item
        while (IncrementalClusterer.ClusterToCheck(
                   item.Embedding, clusters, refused, joinSimilarity: 0.85, checkSimilarity: 0.62, maxChecks: 2) is { } clusterId)
        {
            var index = clusters.FindIndex(c => c.Id == clusterId);
            if (await judge.SameAsync(item, clusterId))
            {
                clusters[index] = IncrementalClusterer.Join(clusters[index], item);
                return null;
            }

            refused.Add(clusterId);   // try the next nearest cluster, if any
        }

        // No judge said yes: similarity decides, and a cluster that refused the item is skipped.
        var result = IncrementalClusterer.Assign(clusters, [item], joinSimilarity: 0.85, (_, cluster) => refused.Contains(cluster));
        return result;
    }

    [Fact]
    public async Task The_pure_functions_join_the_cluster_the_judge_accepts()
    {
        var clusters = TwoCloseCalls();
        var judge = new FakeJudge(accepts: 2);

        var result = await PlaceAsync(clusters, judge, new EmbeddedItem(9, [1f, 0f, 0f]));

        Assert.Null(result);
        Assert.Equal([1L, 2L], judge.Asked);
        Assert.Equal([4, 5], clusters.Select(c => c.Count));
    }

    [Fact]
    public async Task The_pure_functions_start_a_cluster_when_every_judge_refuses()
    {
        var clusters = TwoCloseCalls();
        var judge = new FakeJudge();

        var result = await PlaceAsync(clusters, judge, new EmbeddedItem(9, [1f, 0f, 0f]));

        Assert.Equal([1L, 2L], judge.Asked);
        Assert.NotNull(result);
        Assert.Equal([9L], Assert.Single(result.Created).ItemIds);
    }

    // ------------------------------------------------------------ docs/configuration.md

    [Fact]
    public void Configuration_lists_every_problem_with_the_options()
    {
        // From docs/configuration.md, "Validation".
        var options = new ClusteringOptions { JoinSimilarity = 0.6, CheckSimilarity = 0.7 };
        IReadOnlyList<string> problems = options.Validate();

        Assert.Equal(
            [
                "EmbeddingModel is required: name the model the embeddings come from.",
                "CheckSimilarity must not exceed JoinSimilarity.",
            ],
            problems);
        Assert.Contains("CheckSimilarity must not exceed JoinSimilarity.", Assert.Throws<ArgumentException>(options.ThrowIfInvalid).Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ docs/how-to/cluster-without-a-model.md

    [Fact]
    public void Cosine_only_options_have_no_ask_zone_and_allow_splits()
    {
        // From docs/how-to/cluster-without-a-model.md.
        var options = ClusteringOptions.CosineOnly("text-embedding-3-small", joinSimilarity: 0.85, mergeSimilarity: 0.88);
        options.Split.Enabled = true;             // regroup loose clusters during MaintainAsync
        options.Split.CohesionThreshold = 0.80;   // loose: members average below 0.80 similarity to the centroid
        options.Split.RegroupSimilarity = 0.85;   // the join threshold used to regroup its members
        options.Split.MinPartSize = 3;            // a part needs at least 3 members to stand alone

        Assert.False(options.HasAskZone);
        Assert.Equal((0.85, 0.85, 0.88, 0.88), (options.JoinSimilarity, options.CheckSimilarity, options.MergeSimilarity, options.MergeCheckSimilarity));
        Assert.Empty(options.Validate());

        // With an ask zone, splits are refused.
        var judged = new ClusteringOptions { EmbeddingModel = "text-embedding-3-small", Split = { Enabled = true } };
        Assert.Contains(judged.Validate(), p => p.StartsWith("Split is for cosine-only setups", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------ docs/how-to/answer-questions-yourself.md

    [Fact]
    public async Task Questions_come_back_as_data_and_an_answer_places_the_item()
    {
        var output = new List<string>();

        // From docs/how-to/answer-questions-yourself.md, "Questions as data".
        var store = new InMemoryClusterStore();
        var clusterer = new Clusterer(store, new ClusteringOptions { EmbeddingModel = "toy-3d" });   // join at 0.83, ask from 0.30

        var step = await clusterer.AddAsync(
        [
            new ClusterItem("ticket-1", new float[] { 1.00f, 0.00f, 0.00f }),
            new ClusterItem("ticket-2", new float[] { 0.82f, 0.57f, 0.00f }),   // 0.82 similar to ticket-1: a close call
        ]);

        foreach (var question in step.Questions.OfType<JoinQuestion>())
        {
            output.Add($"question {question.Id}: does {question.ItemId} belong in cluster {question.ClusterId}? " +
                $"similarity {question.Similarity:0.00}, examples: {string.Join(", ", question.ExampleItemIds)}");
        }

        output.Add($"{await clusterer.GetClusterOfAsync("ticket-2")}");   // null (empty) while the question is open

        // Later, in this process or another one over the same store:
        var open = await clusterer.GetOpenQuestionsAsync();
        var answered = await clusterer.AnswerAsync([Verdict.Same(open[0].Id)]);
        output.Add($"{answered.Placements[0]}");

        Assert.Equal(
            [
                $"question 1: does ticket-2 belong in cluster 1? similarity {0.82:0.00}, examples: ticket-1",
                "",
                "Placement { ItemId = ticket-2, ClusterId = 1, Reason = Judge }",
            ],
            output);
    }

    [Fact]
    public async Task A_refusal_starts_a_cluster_of_its_own_when_no_other_cluster_is_close()
    {
        // From docs/how-to/answer-questions-yourself.md: the same question answered "different".
        var clusterer = new Clusterer(new InMemoryClusterStore(), new ClusteringOptions { EmbeddingModel = "toy-3d" });
        var step = await clusterer.AddAsync(
        [
            new ClusterItem("ticket-1", new float[] { 1.00f, 0.00f, 0.00f }),
            new ClusterItem("ticket-2", new float[] { 0.82f, 0.57f, 0.00f }),
        ]);

        var answered = await clusterer.AnswerAsync([Verdict.Different(step.Questions[0].Id)]);

        Assert.Equal(new Placement("ticket-2", 2, PlacementReason.NewCluster), Assert.Single(answered.Placements));
    }

    [Fact]
    public async Task A_rule_can_be_the_judge_of_a_settle_loop()
    {
        var output = new List<string>();

        // From docs/how-to/answer-questions-yourself.md, "Answer live with your own judge".
        var product = new Dictionary<string, string>
        {
            ["ticket-1"] = "boiler",
            ["ticket-2"] = "boiler",
        };

        // Two tickets are the same only if they are about the same product.
        var judge = new DelegateJudge(question =>
        {
            if (question is not JoinQuestion join)
            {
                return Verdict.Unanswerable(question.Id);   // merges: similarity decides
            }

            var offered = join.OtherChoices.Select(c => (c.ClusterId, c.ExampleItemIds)).Prepend((join.ClusterId, join.ExampleItemIds));
            foreach (var (clusterId, examples) in offered)
            {
                if (examples.Count > 0 && product[examples[0]] == product[join.ItemId])
                {
                    return Verdict.Picked(join.Id, clusterId);
                }
            }

            return Verdict.Different(join.Id);   // none of them
        });

        var clusterer = new Clusterer(new InMemoryClusterStore(), new ClusteringOptions { EmbeddingModel = "toy-3d" });
        var step = await clusterer.SettleAsync(
        [
            new ClusterItem("ticket-1", new float[] { 1.00f, 0.00f, 0.00f }),
            new ClusterItem("ticket-2", new float[] { 0.82f, 0.57f, 0.00f }),
        ], judge);

        foreach (var placement in step.Placements)
        {
            output.Add($"{placement.ItemId} -> cluster {placement.ClusterId} ({placement.Reason})");
        }

        Assert.Equal(["ticket-1 -> cluster 1 (NewCluster)", "ticket-2 -> cluster 1 (Judge)"], output);
    }

    // ------------------------------------------------------------ docs/how-to/label-clusters.md

    [Fact]
    public async Task A_label_set_by_hand_is_kept_with_the_size_it_was_set_at()
    {
        var clusterer = await GettingStartedClustererAsync();

        // From docs/how-to/label-clusters.md, "Set labels yourself".
        await clusterer.SetLabelAsync(1, "Heating does not work");
        var cluster = await clusterer.GetClusterAsync(1);
        var line = $"{cluster!.Label}: {cluster.Count} items, labeled at {cluster.LabeledAtCount}";

        Assert.Equal("Heating does not work: 3 items, labeled at 3", line);
        Assert.False(await clusterer.SetLabelAsync(99, "No such cluster"));
        Assert.True(await clusterer.SetLabelAsync(2, "Heating"));   // a merged id leads to the survivor
        Assert.Equal("Heating", (await clusterer.GetClusterAsync(1))!.Label);
    }

    // ------------------------------------------------------------ docs/how-to/remove-items.md

    [Fact]
    public async Task A_removed_item_leaves_its_cluster_and_a_returning_one_joins_the_empty_cluster()
    {
        var output = new List<string>();

        // From docs/how-to/remove-items.md.
        var clusterer = new Clusterer(new InMemoryClusterStore(), ClusteringOptions.CosineOnly("toy-3d", joinSimilarity: 0.85));
        await clusterer.AddAsync(
        [
            new ClusterItem("ticket-1", new float[] { 1.00f, 0.00f, 0.00f }),
            new ClusterItem("ticket-3", new float[] { 0.97f, 0.26f, 0.00f }),
            new ClusterItem("ticket-4", new float[] { 0.00f, 0.00f, 1.00f }),
        ]);

        // The author of ticket-4 asked for it to be deleted.
        await clusterer.RemoveAsync(["ticket-4"]);

        var cluster = await clusterer.GetClusterAsync(2);
        output.Add($"cluster {cluster!.Id}: {cluster.Count} items");
        output.Add($"{await clusterer.GetClusterOfAsync("ticket-4") is null}");

        // A later ticket like it joins the same cluster again, by similarity.
        var step = await clusterer.AddAsync([new ClusterItem("ticket-9", new float[] { 0.00f, 0.10f, 1.00f })]);
        output.Add($"{step.Placements[0]}");

        Assert.Equal(
            [
                "cluster 2: 0 items",
                "True",
                "Placement { ItemId = ticket-9, ClusterId = 2, Reason = Similarity }",
            ],
            output);
    }

    // ------------------------------------------------------------ docs/how-to/dependency-injection.md

    [Fact]
    public void Dependency_injection_binds_the_options_from_the_Akinly_section()
    {
        // The appsettings.json of docs/how-to/dependency-injection.md, as configuration keys.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Akinly:EmbeddingModel"] = "bge-m3",
                ["Akinly:JoinSimilarity"] = "0.96",
                ["Akinly:CheckSimilarity"] = "0.54",
                ["Akinly:MergeSimilarity"] = "0.88",
                ["Akinly:MergeCheckSimilarity"] = "0.75",
                ["Akinly:MaxOpenQuestions"] = "200",
                ["Akinly:FailedAnswers:MaxAttempts"] = "3",
                ["Akinly:FailedAnswers:RetryDelay"] = "00:00:02",
            })
            .Build();

        // From docs/how-to/dependency-injection.md.
        var services = new ServiceCollection();
        services.AddAkinly(configuration.GetSection(ClusteringOptions.SectionName));
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<Clusterer>().Options;
        Assert.Equal(("bge-m3", 0.54, 0.75, 200, TimeSpan.FromSeconds(2)), (options.EmbeddingModel, options.CheckSimilarity, options.MergeCheckSimilarity, options.MaxOpenQuestions, options.FailedAnswers.RetryDelay));
        Assert.IsType<InMemoryClusterStore>(provider.GetRequiredService<IClusterStore>());
    }

    // ------------------------------------------------------------ docs/how-to/opentelemetry.md

    [Fact]
    public async Task A_meter_listener_sees_the_clusterers_counters_with_their_tags()
    {
        var output = new ConcurrentQueue<string>();

        // From docs/how-to/opentelemetry.md, "Without OpenTelemetry".
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == AkinlyTelemetry.Name)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            output.Enqueue($"{instrument.Name} +{value} {string.Join(", ", tags.ToArray().Select(t => $"{t.Key}={t.Value}"))}"));
        listener.Start();

        var clusterer = new Clusterer(new InMemoryClusterStore(), ClusteringOptions.CosineOnly("toy-3d", joinSimilarity: 0.85));
        await clusterer.AddAsync([new ClusterItem("ticket-1", new float[] { 1.00f, 0.00f, 0.00f })]);

        // Other tests run clusterers at the same time, so look for this one's line among theirs.
        Assert.Contains("akinly.items.placed +1 reason=new_cluster", output);
    }

    // ------------------------------------------------------------ docs/troubleshooting.md

    [Fact]
    public async Task A_store_refuses_a_clusterer_configured_for_another_model()
    {
        var store = new InMemoryClusterStore();
        await new Clusterer(store, ClusteringOptions.CosineOnly("model-a", 0.85)).AddAsync([new ClusterItem("x", new float[] { 1, 0, 0 })]);

        var mismatch = await Assert.ThrowsAsync<EmbeddingModelMismatchException>(
            () => new Clusterer(store, ClusteringOptions.CosineOnly("model-b", 0.85)).AddAsync([new ClusterItem("y", new float[] { 1, 0, 0 })]));
        var length = await Assert.ThrowsAsync<EmbeddingModelMismatchException>(
            () => new Clusterer(store, ClusteringOptions.CosineOnly("model-a", 0.85)).AddAsync([new ClusterItem("y", new float[] { 1, 0 })]));
        var again = await Assert.ThrowsAsync<ArgumentException>(
            () => new Clusterer(store, ClusteringOptions.CosineOnly("model-a", 0.85)).AddAsync([new ClusterItem("x", new float[] { 1, 0, 0 })]));

        // The messages docs/troubleshooting.md quotes.
        Assert.StartsWith("The store holds embeddings from 'model-a', but the clusterer is configured for 'model-b'.", mismatch.Message, StringComparison.Ordinal);
        Assert.Equal("Item 'y' has an embedding of 2 dimensions where the store holds 3.", length.Message);
        Assert.StartsWith("Item 'x' is already in the store; remove it before adding it again.", again.Message, StringComparison.Ordinal);
    }

    /// <summary>docs/how-to/merge-policy.md: a policy as a function lets the cluster with a public page keep its id.</summary>
    [Fact]
    public async Task A_merge_policy_function_chooses_which_cluster_keeps_its_id()
    {
        var published = new HashSet<long> { 2 };
        var policy = new DelegateMergePolicy((first, second) =>
            published.Contains(second.Id) && !published.Contains(first.Id) ? MergeRule.KeepSecond
            : published.Contains(first.Id) && !published.Contains(second.Id) ? MergeRule.KeepFirst
            : MergeRule.Allow);

        var options = ClusteringOptions.CosineOnly("toy-3d", joinSimilarity: 0.9, mergeSimilarity: 0.85);
        var clusterer = new Clusterer(new InMemoryClusterStore(), options, mergePolicy: policy);
        await clusterer.AddAsync(
        [
            new ClusterItem("ticket-1", new float[] { 1.00f, 0.00f, 0.00f }),
            new ClusterItem("ticket-2", new float[] { 1.00f, 0.02f, 0.00f }),
            new ClusterItem("ticket-3", new float[] { 0.87f, 0.50f, 0.00f }),
        ]);

        var step = await clusterer.MaintainAsync();

        Assert.Equal(new ClusterMerge(1, 2, PlacementReason.Similarity), Assert.Single(step.Merges));
    }
}
