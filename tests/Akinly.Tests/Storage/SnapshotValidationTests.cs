using System.Text;
using Akinly.Storage;

namespace Akinly.Tests.Storage;

/// <summary>Snapshots that are well-formed JSON but contradict themselves: each is refused on import.</summary>
public sealed class SnapshotValidationTests
{
    private const string Info = """{"Version":1,"EmbeddingModel":"m","Dimensions":2,"NextClusterId":3,"NextQuestionId":3,"NextItemSequence":3,"OpenQuestions":0,"WaitingItems":0}""";
    private const string Cluster = """{"Id":1,"Centroid":[1,0],"Sum":[1,0],"Count":1,"Label":null,"MergedIntoId":null,"Changed":true,"RemovalsSinceRecompute":0,"RefusedBy":[]}""";
    private const string Item = """{"Id":"a","Sequence":1,"Embedding":[1,0],"ClusterId":1,"State":"Placed","RefusedClusterIds":[],"OpenQuestionId":null,"JudgeUnavailable":false}""";

    private static string Snapshot(string clusters, string items, string questions = "", string info = Info) =>
        $$"""{"SchemaVersion":1,"Info":{{info}},"Clusters":[{{clusters}}],"Items":[{{items}}],"Questions":[{{questions}}],"MergeChecks":[]}""";

    public static TheoryData<string, string> Contradictions => new()
    {
        { "the baseline is valid", Snapshot(Cluster, Item) },
        { "an item outside the sequence range", Snapshot(Cluster, Item.Replace("\"Sequence\":1", "\"Sequence\":9", StringComparison.Ordinal)) },
        { "an item in a cluster that does not exist", Snapshot(Cluster, Item.Replace("\"ClusterId\":1", "\"ClusterId\":2", StringComparison.Ordinal)) },
        { "a question outside the id range", Snapshot(Cluster, Item, """{"Question":{"kind":"join","ItemId":"x","ClusterId":1,"ClusterLabel":null,"ExampleItemIds":[],"Id":{"Value":7},"Similarity":0.7},"State":"Closed","FailedAttempts":0,"Outcome":null}""") },
        { "a centroid that is not finite", Snapshot(Cluster.Replace("\"Centroid\":[1,0]", "\"Centroid\":[1e39,0]", StringComparison.Ordinal), Item) },
        { "a sum of the wrong length", Snapshot(Cluster.Replace("\"Sum\":[1,0]", "\"Sum\":[1]", StringComparison.Ordinal), Item) },
        { "an asking item without its question", Snapshot(Cluster.Replace("\"Count\":1", "\"Count\":0", StringComparison.Ordinal), Item.Replace("\"ClusterId\":1,\"State\":\"Placed\"", "\"ClusterId\":null,\"State\":\"Asking\"", StringComparison.Ordinal).Replace("\"OpenQuestionId\":null", "\"OpenQuestionId\":{\"Value\":1}", StringComparison.Ordinal)) },
        { "a waiting item counted wrong", Snapshot(Cluster.Replace("\"Count\":1", "\"Count\":0", StringComparison.Ordinal), Item.Replace("\"ClusterId\":1,\"State\":\"Placed\"", "\"ClusterId\":null,\"State\":\"Waiting\"", StringComparison.Ordinal)) },
        { "a placed item in a merged cluster", Snapshot(Cluster.Replace("\"MergedIntoId\":null", "\"MergedIntoId\":2", StringComparison.Ordinal) + "," + Cluster.Replace("\"Id\":1", "\"Id\":2", StringComparison.Ordinal).Replace("\"Count\":1", "\"Count\":0", StringComparison.Ordinal), Item) },
        { "an item without an id", Snapshot(Cluster, Item.Replace("\"Id\":\"a\"", "\"Id\":\"\"", StringComparison.Ordinal)) },
        { "a question without its kind", Snapshot(Cluster, Item, """{"Question":{"Id":{"Value":1},"Similarity":0.7},"State":"Closed","FailedAttempts":0,"Outcome":null}""") },
    };

    [Theory]
    [MemberData(nameof(Contradictions))]
    public async Task Each_contradiction_is_refused(string why, string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        if (why == "the baseline is valid")
        {
            var store = await InMemoryClusterStore.ImportAsync(stream);
            Assert.Equal(1, (await store.GetInfoAsync(default)).Version);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => InMemoryClusterStore.ImportAsync(stream));
        }
    }
}
