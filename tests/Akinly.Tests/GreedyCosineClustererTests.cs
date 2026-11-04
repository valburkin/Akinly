namespace Akinly.Tests;

public sealed class GreedyCosineClustererTests
{
    private static readonly GreedyCosineClusterer Clusterer = new();

    /// <summary>An id that tells two kinds of items apart, as a caller mixing them would use.</summary>
    private readonly record struct Unit(char Kind, long Id)
    {
        public static Unit Post(long id) => new('p', id);

        public static Unit Comment(long id) => new('c', id);
    }

    /// <summary>A vector near <paramref name="axis"/>, tilted slightly by <paramref name="jitter"/>.</summary>
    private static float[] Near(int axis, float jitter, int dimensions = 8)
    {
        var v = new float[dimensions];
        v[axis] = 1f;
        v[(axis + 1) % dimensions] = jitter;
        return v;
    }

    [Fact]
    public void Similar_items_join_one_cluster_and_dissimilar_ones_start_another()
    {
        var items = new List<(Unit, float[])>
        {
            (Unit.Post(1), Near(0, 0.05f)),
            (Unit.Post(2), Near(0, 0.10f)),
            (Unit.Comment(3), Near(0, 0.00f)),
            (Unit.Post(4), Near(4, 0.05f)),
            (Unit.Post(5), Near(4, 0.02f)),
        };

        var clusters = Clusterer.Cluster(items, minClusterSize: 2, similarityThreshold: 0.9);

        Assert.Equal(2, clusters.Count);
        Assert.Equal([Unit.Post(1), Unit.Post(2), Unit.Comment(3)], clusters[0].ItemIds);
        Assert.Equal([Unit.Post(4), Unit.Post(5)], clusters[1].ItemIds);
        Assert.Equal([0, 1], clusters.Select(c => c.Id));
    }

    [Fact]
    public void Clusters_below_the_minimum_size_are_dropped()
    {
        var items = new List<(long, float[])>
        {
            (1, Near(0, 0.01f)),
            (2, Near(0, 0.02f)),
            (3, Near(5, 0.01f)),
        };

        var clusters = Clusterer.Cluster(items, minClusterSize: 2, similarityThreshold: 0.9);

        var only = Assert.Single(clusters);
        Assert.Equal([1L, 2L], only.ItemIds);
    }

    [Fact]
    public void Vectors_are_compared_by_direction_not_length()
    {
        var items = new List<(long, float[])>
        {
            (1, [1f, 0f, 0f]),
            (2, [50f, 1f, 0f]),
        };

        var clusters = Clusterer.Cluster(items, minClusterSize: 2, similarityThreshold: 0.99);

        Assert.Equal(2, Assert.Single(clusters).ItemIds.Count);
    }

    [Fact]
    public void The_centroid_is_unit_length_and_the_sample_holds_at_most_ten_distinct_members()
    {
        var items = Enumerable.Range(1, 30)
            .Select(i => ((long)i, Near(2, i / 1000f)))
            .ToList();

        var cluster = Assert.Single(Clusterer.Cluster(items, minClusterSize: 1, similarityThreshold: 0.9));

        var norm = Math.Sqrt(cluster.Centroid.Sum(x => (double)x * x));
        Assert.Equal(1.0, norm, 5);
        Assert.Equal(10, cluster.Sample.Count);
        Assert.Equal(cluster.Sample.Count, cluster.Sample.Distinct().Count());
    }

    [Fact]
    public void The_sample_holds_both_ends_of_the_cluster()
    {
        var items = Enumerable.Range(1, 30)
            .Select(i => ((long)i, Near(2, i / 100f)))
            .ToList();

        var cluster = Assert.Single(Clusterer.Cluster(items, minClusterSize: 1, similarityThreshold: 0.9));

        // The centroid sits mid-tilt, so the least and the most tilted members are the
        // farthest from it: the sample shows them beside the typical ones.
        Assert.Contains(15L, cluster.Sample);
        Assert.Contains(30L, cluster.Sample);
        Assert.Contains(1L, cluster.Sample);
    }

    [Fact]
    public void No_items_give_no_clusters()
    {
        Assert.Empty(Clusterer.Cluster(Array.Empty<(long, float[])>(), minClusterSize: 1));
    }
}
