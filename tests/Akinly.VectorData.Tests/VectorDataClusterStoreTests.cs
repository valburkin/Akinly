using System.Text.Json;
using Akinly.Storage;
using Akinly.VectorData.Records;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.Connectors.InMemory;

namespace Akinly.VectorData.Tests;

public sealed class VectorDataClusterStoreTests
{
    [Fact]
    public void Uuid_keys_are_name_based_version_5_and_stable()
    {
        var keys = new GuidKeys();

        var cluster = keys.Cluster(42);

        Assert.Equal(cluster, keys.Cluster(42));
        Assert.NotEqual(cluster, keys.Cluster(43));
        Assert.NotEqual(cluster, keys.Item("42"));
        Assert.Equal(keys.Check(1, 2), keys.Check(2, 1));
        Assert.Equal(5, cluster.Version);
        Assert.Equal(0x80, cluster.ToByteArray(bigEndian: true)[8] & 0xC0);   // the RFC 4122 variant
    }

    [Fact]
    public void Text_keys_say_what_they_are_in_characters_every_database_accepts()
    {
        var keys = new StringKeys();

        Assert.Equal(
            ("cluster-7", "item-eA", "question-3", "check-1-2", "meta-info"),
            (keys.Cluster(7), keys.Item("x"), keys.Question(new QuestionId(3)), keys.Check(2, 1), keys.Meta("info")));
        foreach (var id in new[] { "a b", "ü/x:y?#", "日本", "+/=", "" })
        {
            Assert.Matches("^item-[A-Za-z0-9_-]*$", keys.Item(id));
        }

        Assert.NotEqual(keys.Item("a"), keys.Item("A"));

        // An id too long to spell out is hashed, into a key no short id can have.
        var longKey = keys.Item(new string('x', Keys<string>.MaxItemIdBytes + 1));
        Assert.Matches("^itemh-[A-Za-z0-9_-]{43}$", longKey);
        Assert.NotEqual(longKey, keys.Item(new string('x', Keys<string>.MaxItemIdBytes + 2)));
        Assert.StartsWith("item-", keys.Item(new string('x', Keys<string>.MaxItemIdBytes)), StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_options_are_refused_with_every_problem()
    {
        var options = new VectorDataClusterStoreOptions
        {
            Dimensions = 0,
            CollectionPrefix = "bad.name",
            NameSeparator = ".",
            DistanceFunction = "",
            PageSize = 0,
            SearchOversampling = -1,
        };

        var exception = Assert.Throws<ArgumentException>(() => new VectorDataClusterStore(new InMemoryVectorStore(), options));

        foreach (var name in new[] { "Dimensions", "CollectionPrefix", "NameSeparator", "DistanceFunction", "PageSize", "SearchOversampling" })
        {
            Assert.Contains(name, exception.Message, StringComparison.Ordinal);
        }

        Assert.Contains("CollectionPrefix", string.Join(" ", new VectorDataClusterStoreOptions { Dimensions = 4, CollectionPrefix = new string('a', 49) }.Validate()), StringComparison.Ordinal);
        Assert.Empty(new VectorDataClusterStoreOptions { Dimensions = 4, CollectionPrefix = new string('a', 48), NameSeparator = "-" }.Validate());
    }

    [Fact]
    public void Collection_names_join_the_prefix_and_separator_and_the_store_keeps_its_own_copy()
    {
        var options = new VectorDataClusterStoreOptions { Dimensions = 4, CollectionPrefix = "support", NameSeparator = "-" };
        var copy = options.Clone();

        options.CollectionPrefix = "changed";

        Assert.Equal("support-items", copy.CollectionName("items"));
        Assert.Equal("changed-items", options.CollectionName("items"));
        Assert.Equal("support_items", new VectorDataClusterStoreOptions { CollectionPrefix = "support" }.CollectionName("items"));
    }

    [Fact]
    public async Task Changing_the_options_after_building_the_store_changes_nothing()
    {
        var vectors = new InMemoryVectorStore();
        var options = new VectorDataClusterStoreOptions { Dimensions = 2, CollectionPrefix = "first" };
        var store = new VectorDataClusterStore(vectors, options);

        options.CollectionPrefix = "second";
        await new Clusterer(store, new ClusteringOptions { EmbeddingModel = "test" }).AddAsync([new ClusterItem("a", new float[] { 1, 0 })]);

        Assert.Equal(1, (await new VectorDataClusterStore(vectors, new VectorDataClusterStoreOptions { Dimensions = 2, CollectionPrefix = "first" }).GetInfoAsync(default)).Version);
    }

    [Fact]
    public void Sums_and_journal_vectors_round_trip_exactly()
    {
        double[] sum = [0.1, -1e-300, double.MaxValue, 1.0 / 3];
        float[] vector = [0.1f, -0f, float.Epsilon, 1f / 3];
        var changes = new ChangeSet(
            StoreInfo.Empty with { Version = 1 },
            [new StoredCluster(1, vector, sum, 1, null, null, true, 0, [])],
            [new StoredItem("é", 1, vector, 1, ItemState.Placed, [], null, false)],
            [],
            [],
            []);

        var json = JsonSerializer.Serialize(changes, StoreJson.Default.ChangeSet);
        var read = JsonSerializer.Deserialize(json, StoreJson.Default.ChangeSet)!;

        Assert.Equal(sum, SumCodec.Decode(SumCodec.Encode(sum)));
        Assert.Equal(sum, read.Clusters[0].Sum.ToArray());
        Assert.Equal(vector, read.Clusters[0].Centroid.ToArray());
        Assert.Equal(vector, read.Items[0].Embedding.ToArray());
        Assert.True(json.All(char.IsAscii));   // so the journal can be cut anywhere
        Assert.Throws<InvalidDataException>(() => SumCodec.Decode(Convert.ToBase64String(new byte[5])));
    }

    [Fact]
    public async Task A_change_set_too_large_to_journal_is_refused_before_anything_is_written()
    {
        // 800 items of 100,000 dimensions (one shared vector, to spare the test's memory): over
        // 400 million characters of journal.
        var vector = new float[100_000];
        vector[0] = 1;
        var store = new VectorDataClusterStore(new InMemoryVectorStore(), new VectorDataClusterStoreOptions { Dimensions = vector.Length });
        var items = Enumerable.Range(0, 800).Select(i => new StoredItem($"i{i}", i + 1, vector, null, ItemState.Waiting, [], null, false)).ToList();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            store.ApplyAsync(new ChangeSet(StoreInfo.Empty with { Version = 1, Dimensions = vector.Length }, [], items, [], [], []), 0, default));

        Assert.Contains("fewer items per call", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, (await store.GetInfoAsync(default)).Version);
    }

    [Fact]
    public async Task Dependency_injection_puts_the_clusterer_over_the_containers_vector_store()
    {
        var services = new ServiceCollection();
        services.AddSingleton<VectorStore>(new InMemoryVectorStore());
        services.AddAkinly(o => o.EmbeddingModel = "test").UseVectorStore(o => o.Dimensions = 2);
        using var provider = services.BuildServiceProvider();

        var clusterer = provider.GetRequiredService<Clusterer>();
        await clusterer.AddAsync([new ClusterItem("a", new float[] { 1, 0 })]);

        Assert.IsType<VectorDataClusterStore>(clusterer.Store);
        Assert.Equal(1L, await clusterer.GetClusterOfAsync("a"));
    }

    [Fact]
    public async Task Dependency_injection_can_use_a_keyed_vector_store()
    {
        var keyed = new InMemoryVectorStore();
        var services = new ServiceCollection();
        services.AddKeyedSingleton<VectorStore>("clusters", keyed);
        services.AddAkinly(o => o.EmbeddingModel = "test").UseVectorStore(o => (o.Dimensions, o.CollectionPrefix) = (2, "keyed"), vectorStoreKey: "clusters");
        using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<Clusterer>().AddAsync([new ClusterItem("a", new float[] { 1, 0 })]);

        var direct = new VectorDataClusterStore(keyed, new VectorDataClusterStoreOptions { Dimensions = 2, CollectionPrefix = "keyed" });
        Assert.Equal(1, (await direct.GetInfoAsync(default)).Version);
    }

    [Fact]
    public void Dependency_injection_refuses_invalid_options_at_registration()
    {
        var builder = new ServiceCollection().AddAkinly(o => o.EmbeddingModel = "test");

        Assert.Throws<ArgumentException>(() => builder.UseVectorStore(o => o.Dimensions = 0));
    }
}
