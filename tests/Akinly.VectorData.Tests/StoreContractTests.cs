using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Akinly.Storage;
using Akinly.VectorData.Records;
using Microsoft.Extensions.VectorData;

namespace Akinly.VectorData.Tests;

/// <summary>
/// The same scenarios against a vector database and against the in-memory reference store: the
/// results must match. Each backend runs the whole set.
/// </summary>
public abstract class StoreContractTests<TBackend>(TBackend backend) : IClassFixture<TBackend>
    where TBackend : class, IBackend
{
    private const int Dimensions = 4;

    private static float[] At(double degrees, double lift = 0)
    {
        var radians = degrees * Math.PI / 180;
        return Vectors.Normalize([(float)Math.Cos(radians), (float)Math.Sin(radians), (float)lift, 0f]);
    }

    private static double AngleFor(double similarity) => Math.Acos(similarity) * 180 / Math.PI;

    private static ClusteringOptions Options() => new()
    {
        EmbeddingModel = "test",
        JoinSimilarity = 0.9,
        CheckSimilarity = 0.6,
        MergeSimilarity = 0.95,
        MergeCheckSimilarity = 0.8,
        FailedAnswers = new FailedAnswerOptions { MaxAttempts = 2, RetryDelay = TimeSpan.Zero },
    };

    private static VectorDataClusterStoreOptions StoreOptions(string prefix, int dimensions = Dimensions) =>
        new() { Dimensions = dimensions, CollectionPrefix = prefix };

    /// <summary>A fresh set of collections in the backend, and its store.</summary>
    private (VectorStore Vectors, VectorDataClusterStore Store, VectorDataClusterStoreOptions Options) Fresh(Action<VectorDataClusterStoreOptions>? configure = null)
    {
        Assert.SkipUnless(backend.Available, "Set AKINLY_CONTAINERS to run the container tests.");
        var vectors = backend.Create();
        var options = StoreOptions("t" + Guid.NewGuid().ToString("N")[..12]);
        configure?.Invoke(options);
        return (vectors, new VectorDataClusterStore(vectors, options), options);
    }

    private static readonly DelegateJudge Judge = new(q => q switch
    {
        JoinQuestion { ItemId: var id } => id.EndsWith('y') ? VerdictKind.Same : VerdictKind.Different,
        _ => VerdictKind.Same,
    });

    private static string Describe(ClusteringStep step) => string.Join(";",
        step.Placements.Select(p => $"{p.ItemId}>{p.ClusterId}:{p.Reason}")
            .Concat(step.Merges.Select(m => $"{m.FromId}=>{m.IntoId}"))
            .Concat(step.Questions.Select(q => $"?{q.Id}")));

    /// <summary>Everything a reader can see: clusters, members, examples and the store's info.</summary>
    private static async Task<List<string>> DumpAsync(IClusterStore store)
    {
        var dump = new List<string>();
        await foreach (var cluster in store.ListClustersAsync(default))
        {
            var members = await store.GetMembersAsync(cluster.Id, default);
            var examples = await store.GetExamplesAsync(cluster.Id, 2, default);
            dump.Add($"cluster {cluster.Id} [{cluster.Label}] {cluster.Count} changed {cluster.Changed}: {string.Join(",", members.Select(m => m.Id))} e.g. {string.Join(",", examples)}");
            dump.Add($"  vectors {Fingerprint(cluster.Centroid)} {Fingerprint(cluster.Sum)} {string.Join(" ", members.Select(m => Fingerprint(m.Embedding)))}");
        }

        var info = await store.GetInfoAsync(default);
        dump.Add($"info {info}");
        dump.Add($"open {string.Join(",", (await store.GetOpenQuestionsAsync(default)).Select(q => q.Question.Id.Value))}");
        dump.Add($"waiting {string.Join(",", (await store.GetWaitingItemsAsync(100, default)).Select(i => i.Id))}");
        return dump;
    }

    /// <summary>The exact bits of a vector, short: two stores agree only if every value does.</summary>
    private static string Fingerprint<T>(ReadOnlyMemory<T> vector)
        where T : struct => Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(vector.Span)))[..12];

    private static StoreInfo Info(long version, int dimensions, long nextCluster) =>
        StoreInfo.Empty with { Version = version, EmbeddingModel = "test", Dimensions = dimensions, NextClusterId = nextCluster };

    private static float[] RandomUnit(int dimensions, Random random) =>
        Vectors.Normalize([.. Enumerable.Range(0, dimensions).Select(_ => (float)(random.NextDouble() - 0.5))]);

    /// <summary>A scripted run: adds, a judge's answers, maintenance, labels and removals.</summary>
    private static async Task<List<string>> RunAsync(IClusterStore store)
    {
        var clusterer = new Clusterer(store, Options());
        var log = new List<string>
        {
            Describe(await clusterer.SettleAsync([new ClusterItem("a", At(0)), new ClusterItem("b", At(5)), new ClusterItem("c", At(90))], Judge)),
            Describe(await clusterer.SettleAsync([new ClusterItem("near-y", At(AngleFor(0.75))), new ClusterItem("near-n", At(-AngleFor(0.75), 0.1))], Judge)),
        };
        await clusterer.SetLabelAsync(1, "Heating repairs");
        log.Add(Describe(await clusterer.SettleAsync([new ClusterItem("d", At(95)), new ClusterItem("e", At(150))], Judge)));
        log.Add(Describe(await clusterer.MaintainAsync()));
        log.Add(Describe(await clusterer.SettleAsync(Judge)));
        await clusterer.RemoveAsync(["b", "nobody"]);
        log.Add(Describe(await clusterer.SettleAsync([new ClusterItem("f", At(2))], Judge)));
        log.AddRange(await DumpAsync(store));
        return log;
    }

    /// <summary>
    /// A harder run: items that wait for a free question, merges, a maintenance cursor that wraps,
    /// duplicate embeddings, and ids with characters no key can hold as they are.
    /// </summary>
    private static async Task<List<string>> RunHarderAsync(IClusterStore store)
    {
        var options = Options();
        options.MaxOpenQuestions = 1;
        options.MaxClustersPerMaintain = 2;
        options.ExamplesPerQuestion = 2;
        options.MergeExamplesPerCluster = 2;
        options.MergeRespectsRefusals = false;
        var clusterer = new Clusterer(store, options);
        var log = new List<string>();

        // Two groups 30 degrees apart (similarity 0.87): the judge keeps them apart when their items
        // arrive, and a merge check joins them later.
        log.Add(Describe(await clusterer.AddAsync([
            new ClusterItem("a", At(0)), new ClusterItem("a dup", At(0)), new ClusterItem("a dup 2", At(0)),
            new ClusterItem("g/ü:1-n", At(30)), new ClusterItem("g?#2-n", At(31)),
            new ClusterItem(new string('x', 300), At(120)),
        ])));

        // Questions for these, one at a time: the rest wait.
        log.Add(Describe(await clusterer.AddAsync([
            new ClusterItem("w1-y", At(AngleFor(0.75))), new ClusterItem("w2-n", At(-AngleFor(0.75))),
            new ClusterItem("w3-y", At(20 + AngleFor(0.75))), new ClusterItem("日本-n", At(120 + AngleFor(0.7))),
        ])));
        log.AddRange(await DumpAsync(store));
        log.Add(Describe(await clusterer.SettleAsync(Judge)));

        for (var pass = 0; pass < 4; pass++)
        {
            log.Add(Describe(await clusterer.MaintainAsync()));
            log.Add(Describe(await clusterer.SettleAsync(Judge)));
        }

        await clusterer.RemoveAsync(["a dup", "g?#2-n"]);
        log.Add(Describe(await clusterer.SettleAsync([new ClusterItem("a dup", At(0))], Judge)));
        log.AddRange(await DumpAsync(store));
        return log;
    }

    [Fact]
    public async Task A_scripted_run_matches_the_in_memory_reference_store()
    {
        var (_, store, _) = Fresh();

        Assert.Equal(await RunAsync(new InMemoryClusterStore()), await RunAsync(store));
    }

    [Fact]
    public async Task Text_keys_work_as_well_as_uuids()
    {
        Assert.SkipUnless(!backend.Available || backend.SupportsTextKeys, "This database accepts no string keys.");
        var (_, store, _) = Fresh(o => o.KeyKind = VectorDataKeyKind.Text);

        Assert.Equal(await RunAsync(new InMemoryClusterStore()), await RunAsync(store));
    }

    [Fact]
    public async Task A_harder_run_with_one_record_per_page_and_no_oversampling_matches_the_reference()
    {
        var (_, store, _) = Fresh(o => (o.PageSize, o.SearchOversampling) = (1, 0));

        var expected = await RunHarderAsync(new InMemoryClusterStore());
        var actual = await RunHarderAsync(store);

        Assert.Equal(expected, actual);
        Assert.Contains(expected, line => line.Contains("=>", StringComparison.Ordinal));   // the run merges
        Assert.Contains(expected, line => line.StartsWith("waiting ", StringComparison.Ordinal) && line.Length > "waiting ".Length);   // and makes items wait
    }

    [Theory]
    [InlineData("parts", false)]
    [InlineData("unjournal", false)]
    [InlineData("parts", true)]
    [InlineData("unjournal", true)]
    [InlineData("journal", false)]
    [InlineData("clusters", false)]
    [InlineData("items", false)]
    [InlineData("questions", false)]
    [InlineData("info", false)]
    [InlineData("journal", true)]
    [InlineData("clusters", true)]
    [InlineData("items", true)]
    [InlineData("questions", true)]
    [InlineData("info", true)]
    public async Task A_write_stopped_at_any_step_is_finished_by_the_next_write(string step, bool restart)
    {
        var (vectors, store, options) = Fresh();
        var clusterer = new Clusterer(store, Options());
        await clusterer.AddAsync([new ClusterItem("a", At(0))]);

        store.OnStep = s => s == step ? Task.FromException(new IOException("Simulated crash.")) : Task.CompletedTask;
        await Assert.ThrowsAsync<IOException>(() => clusterer.AddAsync([new ClusterItem("b", At(90)), new ClusterItem("a2", At(1))]));
        store.OnStep = null;

        var next = restart ? new VectorDataClusterStore(vectors, options) : store;
        await new Clusterer(next, Options()).AddAsync([new ClusterItem("c", At(180))]);

        var reference = new InMemoryClusterStore();
        var referenceClusterer = new Clusterer(reference, Options());
        await referenceClusterer.AddAsync([new ClusterItem("a", At(0))]);
        if (step != "parts")
        {
            // Stopped before its journal's header, a write never happened; after it, it is finished.
            await referenceClusterer.AddAsync([new ClusterItem("b", At(90)), new ClusterItem("a2", At(1))]);
        }

        await referenceClusterer.AddAsync([new ClusterItem("c", At(180))]);
        Assert.Equal(await DumpAsync(reference), await DumpAsync(next));
    }

    [Fact]
    public async Task A_journal_that_cannot_be_applied_stops_every_write_until_it_can()
    {
        var (_, store, _) = Fresh();
        var clusterer = new Clusterer(store, Options());
        await clusterer.AddAsync([new ClusterItem("a", At(0))]);

        store.OnStep = s => s == "items" ? Task.FromException(new IOException("The database refuses the write.")) : Task.CompletedTask;
        await Assert.ThrowsAsync<IOException>(() => clusterer.AddAsync([new ClusterItem("b", At(90))]));
        var stuck = await Assert.ThrowsAsync<InvalidOperationException>(() => clusterer.AddAsync([new ClusterItem("c", At(180))]));
        Assert.IsType<IOException>(stuck.InnerException);

        store.OnStep = null;
        await clusterer.AddAsync([new ClusterItem("c", At(180))]);

        Assert.Equal(["a", "b", "c"], (await store.GetItemsAsync(["a", "b", "c"], default)).Select(i => i.Id));
        Assert.Equal(3, (await store.GetInfoAsync(default)).Version);
    }

    [Fact]
    public async Task Change_sets_the_database_cannot_store_are_refused_before_anything_is_written()
    {
        var (_, store, _) = Fresh();
        var clusterer = new Clusterer(store, Options());
        await clusterer.AddAsync([new ClusterItem("a", At(0))]);
        var next = StoreInfo.Empty with { Version = 2, Dimensions = Dimensions };
        StoredItem Item(string id, float[] embedding) => new(id, 9, embedding, null, ItemState.Waiting, [], null, false);

        await Assert.ThrowsAsync<ClusterStoreConflictException>(() => store.ApplyAsync(new ChangeSet(next with { Version = 1 }, [], [], [], [], []), 0, default));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ApplyAsync(new ChangeSet(next, [], [], [], [], []), 5, default));
        await Assert.ThrowsAsync<EmbeddingModelMismatchException>(() => store.ApplyAsync(new ChangeSet(next with { Dimensions = 7 }, [], [], [], [], []), 1, default));
        await Assert.ThrowsAsync<EmbeddingModelMismatchException>(() => store.ApplyAsync(new ChangeSet(next, [], [Item("x", new float[3])], [], [], []), 1, default));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ApplyAsync(new ChangeSet(next, [], [Item("x\0y", At(0))], [], [], []), 1, default));

        Assert.Equal(1, (await store.GetInfoAsync(default)).Version);
        Assert.Empty(await store.GetItemsAsync(["x", "x\0y"], default));
    }

    [Fact]
    public async Task Reads_skip_unknown_ids_and_empty_requests()
    {
        var (_, store, _) = Fresh();

        Assert.Equal(StoreInfo.Empty, await store.GetInfoAsync(default));
        Assert.Empty(await store.GetClustersAsync([], default));
        Assert.Empty(await store.GetClustersAsync([404], default));
        Assert.Empty(await store.GetItemsAsync([], default));
        Assert.Empty(await store.GetQuestionsAsync([], default));
        Assert.Empty(await store.GetMergeChecksAsync([], default));
        Assert.Empty(await store.NearestClustersAsync(At(0), 0, default));
        Assert.Empty(await store.NearestClustersAsync(new float[3], 5, default));
        Assert.Empty(await store.GetExamplesAsync(404, 3, default));
        Assert.Empty(await store.GetWaitingItemsAsync(10, default));
        Assert.Empty(await store.GetOpenQuestionsAsync(default));
    }

    /// <summary>A cluster made directly, with its members' sum.</summary>
    private static StoredCluster Cluster(long id, IReadOnlyList<float[]> members)
    {
        var sum = new double[members[0].Length];
        foreach (var member in members)
        {
            for (var i = 0; i < sum.Length; i++)
            {
                sum[i] += member[i];
            }
        }

        var centroid = Vectors.Normalize([.. sum.Select(v => (float)v)]);
        return new StoredCluster(id, centroid, sum, members.Count, null, null, true, 0, []);
    }

    private static StoredItem Placed(string id, long sequence, float[] embedding, long clusterId) =>
        new(id, sequence, embedding, clusterId, ItemState.Placed, [], null, false);

    private static async Task ApplyBothAsync(IClusterStore first, IClusterStore second, ChangeSet changes)
    {
        await first.ApplyAsync(changes, changes.Info.Version - 1, default);
        await second.ApplyAsync(changes, changes.Info.Version - 1, default);
    }

    [Theory]
    [InlineData("parts")]
    [InlineData("journal")]
    [InlineData("clusters")]
    [InlineData("info")]
    [InlineData("unjournal")]
    public async Task A_change_set_journaled_in_several_parts_and_stopped_at_any_step_is_finished_by_a_new_process(string step)
    {
        // 40 clusters of 1,536 dimensions: about 1 MB of journal, so more than one part.
        const int dimensions = 1536;
        var (vectors, store, options) = Fresh(o => o.Dimensions = dimensions);
        var random = new Random(5);
        var clusters = Enumerable.Range(1, 40).Select(id => Cluster(id, [RandomUnit(dimensions, random)])).ToList();
        var first = new ChangeSet(Info(1, dimensions, 41), clusters, [], [], [], []);
        store.OnStep = s => s == step ? Task.FromException(new IOException("Simulated crash.")) : Task.CompletedTask;
        await Assert.ThrowsAsync<IOException>(() => store.ApplyAsync(first, 0, default));

        var restarted = new VectorDataClusterStore(vectors, options);
        if (step == "parts")
        {
            // Stopped before its header: never journaled, so nothing to finish; the parts left
            // behind are overwritten by the next journal.
            Assert.Equal(0, (await restarted.GetInfoAsync(default)).Version);
            await restarted.ApplyAsync(first, 0, default);
        }

        await restarted.ApplyAsync(new ChangeSet(Info(2, dimensions, 41), [], [], [], [], []), 1, default);

        Assert.Equal(2, (await restarted.GetInfoAsync(default)).Version);
        var read = await restarted.GetClustersAsync(clusters.Select(c => c.Id), default);
        Assert.Equal(clusters.Select(c => Fingerprint(c.Sum)), read.Select(c => Fingerprint(c.Sum)));
        Assert.Equal(clusters.Select(c => Fingerprint(c.Centroid)), read.Select(c => Fingerprint(c.Centroid)));
    }

    [Fact]
    public async Task Text_the_database_cannot_store_and_repeated_records_are_refused_before_anything_is_written()
    {
        var (_, store, _) = Fresh();
        await new Clusterer(store, Options()).AddAsync([new ClusterItem("a", At(0))]);
        var next = Info(2, Dimensions, 2);
        StoredItem Item(string id) => new(id, 9, At(0), null, ItemState.Waiting, [], null, false);
        StoredCluster Labeled(string label) => Cluster(1, [At(0)]) with { Label = label };

        foreach (var changes in new[]
        {
            new ChangeSet(next, [], [], ["x\0y"], [], []),
            new ChangeSet(next, [], [Item("x\uD800y")], [], [], []),
            new ChangeSet(next, [], [Item("x\uDC00")], [], [], []),
            new ChangeSet(next, [], [Item("x\uD800")], [], [], []),
            new ChangeSet(next, [Labeled("a\0b")], [], [], [], []),
            new ChangeSet(next, [Labeled("\uD800")], [], [], [], []),
            new ChangeSet(next, [], [Item("same"), Item("same")], [], [], []),
            new ChangeSet(next, [Cluster(1, [At(0)]), Cluster(1, [At(1)])], [], [], [], []),
        })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => store.ApplyAsync(changes, 1, default));
        }

        Assert.Equal(1, (await store.GetInfoAsync(default)).Version);
        await store.ApplyAsync(new ChangeSet(next, [Labeled("Ünïcödé 🎉 label")], [Item("😀 emoji id")], [], [], []), 1, default);
        Assert.Equal("Ünïcödé 🎉 label", (await store.GetClustersAsync([1], default))[0].Label);
    }

    [Fact]
    public async Task Ids_too_long_to_be_keys_are_hashed_and_still_round_trip()
    {
        foreach (var kind in backend.SupportsTextKeys ? new[] { VectorDataKeyKind.Uuid, VectorDataKeyKind.Text } : [VectorDataKeyKind.Uuid])
        {
            var (_, store, _) = Fresh(o => o.KeyKind = kind);
            var longId = string.Concat(Enumerable.Repeat("ünïcode-id/", 400));
            var item = Placed(longId, 1, At(0), 1);

            await store.ApplyAsync(new ChangeSet(Info(1, Dimensions, 2), [Cluster(1, [At(0)])], [item], [], [], []), 0, default);

            Assert.Equal([longId], (await store.GetItemsAsync([longId], default)).Select(i => i.Id));
            Assert.Equal([longId], (await store.GetMembersAsync(1, default)).Select(i => i.Id));
        }
    }

    [Fact]
    public async Task A_write_another_process_left_halfway_is_finished_by_a_process_that_wrote_before()
    {
        // Process A writes, then B (taking over) stops halfway; A's next write must finish B's
        // change set before its own, though A already looked for a journal once.
        var (vectors, first, options) = Fresh();
        var clustererA = new Clusterer(first, Options());
        await clustererA.AddAsync([new ClusterItem("a", At(0))]);
        var second = new VectorDataClusterStore(vectors, options)
        {
            OnStep = s => s == "items" ? Task.FromException(new IOException("Simulated crash.")) : Task.CompletedTask,
        };
        await Assert.ThrowsAsync<IOException>(() => new Clusterer(second, Options()).AddAsync([new ClusterItem("b", At(90))]));

        await clustererA.AddAsync([new ClusterItem("c", At(180))]);

        Assert.Equal(3, (await first.GetInfoAsync(default)).Version);
        Assert.Equal(["a", "b", "c"], (await first.GetItemsAsync(["a", "b", "c"], default)).Select(i => i.Id));
        Assert.Equal(3, await first.ListClustersAsync(default).CountAsync());
    }

    [Fact]
    public async Task Two_writes_at_once_on_one_store_cannot_both_win()
    {
        var (_, store, _) = Fresh();
        var change = new ChangeSet(Info(1, Dimensions, 1), [], [], [], [], []);

        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Record.ExceptionAsync(() => store.ApplyAsync(change, 0, default)).AsTask()));

        Assert.Single(results, r => r is null);
        Assert.Single(results, r => r is ClusterStoreConflictException);
    }

    [Fact]
    public async Task Examples_tied_at_the_cut_are_taken_in_sequence_order_like_the_reference()
    {
        var (_, store, _) = Fresh(o => o.SearchOversampling = 0);
        var reference = new InMemoryClusterStore();
        var duplicate = At(30);
        var items = new[]
        {
            Placed("dup-late", 50, duplicate, 1), Placed("center-1", 1, At(10), 1), Placed("dup-early", 7, duplicate, 1),
            Placed("dup-mid", 20, duplicate, 1), Placed("center-2", 2, At(10), 1), Placed("dup-last", 60, duplicate, 1),
        };
        var info = StoreInfo.Empty with { Version = 1, EmbeddingModel = "test", Dimensions = Dimensions, NextClusterId = 2, NextItemSequence = 61 };

        await ApplyBothAsync(store, reference, new ChangeSet(info, [Cluster(1, [.. items.Select(i => i.Embedding.ToArray())])], items, [], [], []));

        for (var count = 1; count <= 6; count++)
        {
            Assert.Equal(await reference.GetExamplesAsync(1, count, default), await store.GetExamplesAsync(1, count, default));
        }
    }

    [Fact]
    public async Task Ids_and_sequences_up_to_2_to_the_53rd_round_trip()
    {
        // Qdrant compares numbers in range filters as doubles, exact up to 2^53: far more ids than
        // any store will hand out.
        var (_, store, _) = Fresh();
        var reference = new InMemoryClusterStore();
        var big = (1L << 53) - 1;
        var info = StoreInfo.Empty with { Version = 1, EmbeddingModel = "test", Dimensions = Dimensions, NextClusterId = big + 1, NextItemSequence = big + 1 };
        var items = new[] { Placed("p", big - 1, At(0), big), Placed("q", big, At(3), big) };

        await ApplyBothAsync(store, reference, new ChangeSet(info, [Cluster(big, [At(0), At(3)])], items, [], [], []));

        Assert.Equal(await DumpAsync(reference), await DumpAsync(store));
        Assert.Equal([big], (await store.GetChangedClustersAsync(big - 1, 10, default)).Select(c => c.Id));
        Assert.Equal(big, (await store.NearestClustersAsync(At(1), 1, default))[0].Cluster.Id);
    }

    [Fact]
    public async Task A_large_store_of_long_vectors_reads_and_writes_in_pages_within_message_limits()
    {
        const int dimensions = 1536;
        const int clusters = 1100;
        var (_, store, _) = Fresh(o => o.Dimensions = dimensions);
        var random = new Random(11);
        var stored = Enumerable.Range(1, clusters)
            .Select(id => Cluster(id, [Vectors.Normalize([.. Enumerable.Range(0, dimensions).Select(_ => (float)(random.NextDouble() - 0.5))])]))
            .ToList();
        var info = StoreInfo.Empty with { Version = 1, EmbeddingModel = "test", Dimensions = dimensions, NextClusterId = clusters + 1 };

        await store.ApplyAsync(new ChangeSet(info, stored, [], [], [], []), 0, default);

        Assert.Equal(clusters, await store.ListClustersAsync(default).CountAsync());
        Assert.Equal(clusters, (await store.GetChangedClustersAsync(0, int.MaxValue, default)).Count);
        var read = await store.GetClustersAsync(stored.Select(c => c.Id), default);
        Assert.Equal(stored.Select(c => c.Id), read.Select(c => c.Id));
        Assert.True(read[500].Sum.Span.SequenceEqual(stored[500].Sum.Span));   // the sums are exact
        Assert.Equal(501, (await store.NearestClustersAsync(stored[500].Centroid, 3, default))[0].Cluster.Id);

        // A search for many neighbors goes in pages too, and gives the exact ranking.
        var many = await store.NearestClustersAsync(stored[500].Centroid, 500, default);
        var expected = stored.Select(c => (c.Id, Similarity: Vectors.Dot(stored[500].Centroid.ToArray(), c.Centroid.ToArray())))
            .OrderByDescending(c => c.Similarity).ThenBy(c => c.Id).Take(500).Select(c => c.Id);
        Assert.Equal(expected, many.Select(n => n.Cluster.Id));
    }

    [Fact]
    public async Task The_store_records_its_schema_and_refuses_one_written_by_a_newer_version()
    {
        var (vectors, store, options) = Fresh();
        await new Clusterer(store, Options()).AddAsync([new ClusterItem("a", At(0))]);
        var meta = vectors.GetCollection<Guid, MetaRecord<Guid>>(options.CollectionName("meta"), VectorDataStore<Guid>.MetaDefinition(options));
        var key = new GuidKeys().Meta("info");

        var record = await meta.GetAsync(key);
        Assert.Equal("info", record!.Name);
        Assert.Equal(VectorDataStore<Guid>.SchemaVersion, JsonSerializer.Deserialize(record!.Body, StoreJson.Default.StoredInfoEnvelope)!.SchemaVersion);

        // A journal written by a newer version is refused before it is read.
        await meta.UpsertAsync(new MetaRecord<Guid> { Key = new GuidKeys().Meta("journal"), Body = JsonSerializer.Serialize(new JournalHeader(2, 1, 99), StoreJson.Default.JournalHeader) });
        var journalError = await Assert.ThrowsAsync<InvalidDataException>(() => new VectorDataClusterStore(vectors, options).ApplyAsync(new ChangeSet(Info(2, Dimensions, 2), [], [], [], [], []), 1, default));
        Assert.Contains("journal", journalError.Message, StringComparison.Ordinal);

        record.Body = JsonSerializer.Serialize(new StoredInfoEnvelope(99, StoreInfo.Empty with { Version = 2 }), StoreJson.Default.StoredInfoEnvelope);
        await meta.UpsertAsync(record);
        await Assert.ThrowsAsync<InvalidDataException>(() => new VectorDataClusterStore(vectors, options).GetInfoAsync(default));
        await Assert.ThrowsAsync<InvalidDataException>(() => new VectorDataClusterStore(vectors, options).ApplyAsync(new ChangeSet(Info(3, Dimensions, 2), [], [], [], [], []), 2, default));
    }
}

public sealed class InMemoryConnectorTests(InMemoryBackend backend) : StoreContractTests<InMemoryBackend>(backend);

public sealed class QdrantTests(QdrantBackend backend) : StoreContractTests<QdrantBackend>(backend);

public sealed class PostgresTests(PostgresBackend backend) : StoreContractTests<PostgresBackend>(backend);
