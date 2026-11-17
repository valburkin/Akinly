using System.Text;
using Akinly.Storage;
using CsCheck;
using static Akinly.Tests.TestVectors;

namespace Akinly.Tests;

/// <summary>
/// Random sequences of adds, answers, maintenance and removals, with the invariants that must
/// hold after every step, and the same sequence giving the same store.
/// </summary>
public sealed class ClustererPropertyTests
{
    /// <summary>One operation: 0 add, 1 answer, 2 maintain, 3 remove; the numbers pick its arguments.</summary>
    private sealed record Op(int Kind, int A, int B);

    private static readonly Gen<Op[]> Ops =
        Gen.Select(Gen.Int[0, 9], Gen.Int[0, 1000], Gen.Int[0, 1000], (kind, a, b) => new Op(kind switch
        {
            <= 4 => 0,
            <= 6 => 1,
            7 => 2,
            _ => 3,
        }, a, b)).Array[1, 50];

    private static ClusteringOptions Options(RefusalRule refusals) => Judged(o =>
    {
        o.MaxOpenQuestions = 3;
        o.MaxChecksPerItem = 2;
        o.FailedAnswers.MaxAttempts = 2;
        o.MergeRefusals = refusals;
        o.CandidateClusters = 4;
    });

    [Fact]
    public async Task Every_sequence_keeps_every_item_in_exactly_one_consistent_state()
    {
        await Ops.SampleAsync(async ops =>
        {
            var store = new InMemoryClusterStore();
            var clusterer = new Clusterer(store, Options(RefusalRule.Block));
            await RunAsync(clusterer, ops, live => CheckInvariantsAsync(store, live));
        }, iter: 250);
    }

    [Fact]
    public async Task The_same_sequence_gives_the_same_store()
    {
        await Ops.SampleAsync(async ops =>
        {
            var first = await ExportAfterAsync(ops);
            var second = await ExportAfterAsync(ops);
            Assert.Equal(first, second);
        }, iter: 60);
    }

    private static async Task<string> ExportAfterAsync(Op[] ops)
    {
        var store = new InMemoryClusterStore();
        await RunAsync(new Clusterer(store, Options(RefusalRule.Ask)), ops, _ => Task.CompletedTask);
        using var stream = new MemoryStream();
        await store.ExportAsync(stream);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>A unit vector on one of a few rays, lifted a little out of the plane, so clusters, close calls and far items all occur.</summary>
    private static float[] Direction(Op op)
    {
        var radians = (((op.A % 12) * 15) + (op.B % 7)) * Math.PI / 180;
        var lift = (op.B % 5) * 0.15;
        return Vectors.Normalize([(float)Math.Cos(radians), (float)Math.Sin(radians), (float)lift, (float)((op.B % 3) * 0.05)]);
    }

    private static async Task RunAsync(Clusterer clusterer, Op[] ops, Func<IReadOnlySet<string>, Task> check)
    {
        var added = new List<string>();
        var live = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (op, index) in ops.Select((op, i) => (op, i)))
        {
            switch (op.Kind)
            {
                case 0:
                    var id = $"item-{index}";
                    await clusterer.AddAsync([Item(id, Direction(op))]);
                    added.Add(id);
                    live.Add(id);
                    break;
                case 1:
                    var open = await clusterer.GetOpenQuestionsAsync();
                    await clusterer.AnswerAsync(open.Select(q => new Verdict(q.Id, (VerdictKind)((q.Id.Value + op.A) % 4))));
                    break;
                case 2:
                    await clusterer.MaintainAsync();
                    break;
                default:
                    if (added.Count > 0)
                    {
                        var victim = added[op.A % added.Count];
                        await clusterer.RemoveAsync([victim]);
                        live.Remove(victim);
                    }

                    break;
            }

            await check(live);
        }
    }

    private static async Task CheckInvariantsAsync(InMemoryClusterStore store, IReadOnlySet<string> live)
    {
        var info = await store.GetInfoAsync(default);
        var clusters = (await store.GetClustersAsync(Enumerable.Range(1, (int)info.NextClusterId).Select(i => (long)i), default)).ToDictionary(c => c.Id);
        var items = await store.GetItemsAsync(live.Order(StringComparer.Ordinal).Append("never-added"), default);
        var open = await store.GetOpenQuestionsAsync(default);
        var waiting = (await store.GetWaitingItemsAsync(int.MaxValue, default)).Select(i => i.Id).ToHashSet();

        // Every item added and not removed is in the store, and nothing else is.
        Assert.Equal(live.Order(StringComparer.Ordinal), items.Select(i => i.Id).Order(StringComparer.Ordinal));
        Assert.Equal(info.OpenQuestions, open.Count);
        Assert.Equal(info.WaitingItems, waiting.Count);
        Assert.Equal(info.WaitingItems, items.Count(i => i.State == ItemState.Waiting));

        // Nothing is stuck: an item waits only while the open questions are at the limit.
        Assert.True(info.WaitingItems == 0 || info.OpenQuestions >= 3, $"{info.WaitingItems} items wait with only {info.OpenQuestions} questions open");

        // Each item is in exactly one consistent state.
        var openJoins = open.Select(q => q.Question).OfType<JoinQuestion>().ToDictionary(q => q.Id);
        foreach (var item in items)
        {
            switch (item.State)
            {
                case ItemState.Placed:
                    Assert.NotNull(item.ClusterId);
                    Assert.True(clusters[item.ClusterId.Value].IsLive);
                    Assert.Null(item.OpenQuestionId);
                    Assert.DoesNotContain(item.Id, waiting);

                    // A merge never carries an item into a cluster that refused it (RefusalRule.Block).
                    foreach (var refused in item.RefusedClusterIds)
                    {
                        Assert.NotEqual(Resolve(clusters, refused), item.ClusterId.Value);
                    }

                    break;
                case ItemState.Asking:
                    Assert.Null(item.ClusterId);
                    Assert.NotNull(item.OpenQuestionId);
                    Assert.Equal(item.Id, openJoins[item.OpenQuestionId.Value].ItemId);
                    Assert.DoesNotContain(item.Id, waiting);
                    break;
                default:
                    Assert.Null(item.ClusterId);
                    Assert.Null(item.OpenQuestionId);
                    Assert.Contains(item.Id, waiting);
                    break;
            }
        }

        // Every open join question belongs to an item that is asking it.
        foreach (var question in openJoins.Values)
        {
            Assert.Contains(items, i => i.Id == question.ItemId && i.OpenQuestionId == question.Id);
        }

        // Counts and sums match the members; merged clusters are empty.
        foreach (var cluster in clusters.Values)
        {
            var members = items.Where(i => i.State == ItemState.Placed && i.ClusterId == cluster.Id).ToList();
            Assert.Equal(cluster.Count, members.Count);
            Assert.Equal(members.Select(m => m.Id).Order(StringComparer.Ordinal), (await store.GetMembersAsync(cluster.Id, default)).Select(m => m.Id).Order(StringComparer.Ordinal));
            for (var d = 0; d < cluster.Sum.Length; d++)
            {
                Assert.Equal(members.Sum(m => (double)m.Embedding.Span[d]), cluster.Sum.Span[d], 4);
            }
        }
    }

    private static long Resolve(Dictionary<long, StoredCluster> clusters, long id)
    {
        while (clusters[id].MergedIntoId is { } next)
        {
            id = next;
        }

        return id;
    }
}
