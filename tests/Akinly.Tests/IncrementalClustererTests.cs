namespace Akinly.Tests;

public sealed class IncrementalClustererTests
{
    /// <summary>A unit vector along <paramref name="axis"/>, tilted by <paramref name="noise"/> toward another axis.</summary>
    private static float[] Axis(int axis, float noise = 0, int noiseAxis = 10)
    {
        var v = new float[16];
        v[axis] = 1;
        v[noiseAxis] = noise;
        return v;
    }

    [Fact]
    public void Items_join_the_nearest_cluster_above_the_threshold_or_start_one()
    {
        var existing = new[] { new ClusterState(100, Axis(0), 5), new ClusterState(200, Axis(1), 2) };
        var result = IncrementalClusterer.Assign(
            existing,
            [
                new EmbeddedItem(1, Axis(0, 0.1f, 11)),
                new EmbeddedItem(2, Axis(1, 0.1f, 12)),
                new EmbeddedItem(3, Axis(2)),
                new EmbeddedItem(4, Axis(2, 0.1f, 13)),
            ],
            joinSimilarity: 0.8);

        Assert.Equal(100, result.Joined[1]);
        Assert.Equal(200, result.Joined[2]);
        var created = Assert.Single(result.Created);
        Assert.Equal([3L, 4L], created.ItemIds);
        Assert.Equal([100L, 200L], result.Updated.Select(u => u.Id));
        Assert.Equal([6, 3], result.Updated.Select(u => u.Count));
    }

    [Fact]
    public void Assignment_is_deterministic()
    {
        var items = Enumerable.Range(0, 30).Select(i => new EmbeddedItem(i, Axis(i % 3, 0.05f * (i % 5), 10 + (i % 4)))).ToList();

        var a = IncrementalClusterer.Assign([], items, 0.8);
        var b = IncrementalClusterer.Assign([], items, 0.8);

        Assert.Equal(a.Created.Select(c => c.ItemIds.ToArray()), b.Created.Select(c => c.ItemIds.ToArray()));
        Assert.Equal(3, a.Created.Count);
    }

    [Fact]
    public void Merges_go_into_the_larger_cluster_and_each_cluster_merges_once()
    {
        var clusters = new[]
        {
            new ClusterState(1, Axis(0), 3),
            new ClusterState(2, Vectors.Normalize(Axis(0, 0.1f)), 9),
            new ClusterState(3, Vectors.Normalize(Axis(0, 0.2f)), 4),
            new ClusterState(4, Axis(5), 50),
        };

        var merge = Assert.Single(IncrementalClusterer.FindMerges(clusters, 0.95));

        Assert.Equal(2, merge.IntoId);
        Assert.Contains(merge.FromId, new long[] { 1, 3 });
    }

    [Fact]
    public void A_loose_cluster_splits_and_a_tight_one_does_not()
    {
        var loose = Enumerable.Range(0, 6).Select(i => new EmbeddedItem(i, Axis(0, 0.05f, 10 + i)))
            .Concat(Enumerable.Range(10, 4).Select(i => new EmbeddedItem(i, Axis(1, 0.05f, i))))
            .ToList();

        var parts = IncrementalClusterer.TrySplit(loose, cohesionThreshold: 0.8, regroupSimilarity: 0.85, minPartSize: 3);

        Assert.NotNull(parts);
        Assert.Equal([6, 4], parts.Select(p => p.ItemIds.Count));

        var tight = Enumerable.Range(0, 10).Select(i => new EmbeddedItem(i, Axis(0, 0.05f, 10 + (i % 5)))).ToList();
        Assert.Null(IncrementalClusterer.TrySplit(tight, 0.8, 0.85, 3));
    }
}
