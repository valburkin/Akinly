namespace Akinly.Tests;

/// <summary>What the clusterer refuses, and the inputs it must handle without being fooled.</summary>
public sealed class IncrementalClustererInputTests
{
    private static float[] Unit(double a, double b) => Vectors.Normalize([(float)a, (float)b]);

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(1.0001)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void A_threshold_outside_0_to_1_is_refused(double threshold)
    {
        ClusterState[] clusters = [new(1, Unit(1, 0), 1), new(2, Unit(0, 1), 1)];
        EmbeddedItem[] items = [new(1, Unit(1, 0)), new(2, Unit(0, 1)), new(3, Unit(1, 1)), new(4, Unit(1, 0.5))];

        Assert.Throws<ArgumentOutOfRangeException>(() => IncrementalClusterer.Assign(clusters, items, threshold));
        Assert.Throws<ArgumentOutOfRangeException>(() => IncrementalClusterer.FindMerges(clusters, threshold));
        Assert.Throws<ArgumentOutOfRangeException>(() => IncrementalClusterer.FindMerges(clusters, [(1, 2)], threshold));
        Assert.Throws<ArgumentOutOfRangeException>(() => IncrementalClusterer.ClusterToCheck(Unit(1, 0), clusters, new HashSet<long>(), threshold, 0.5, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => IncrementalClusterer.ClusterToCheck(Unit(1, 0), clusters, new HashSet<long>(), 0.9, threshold, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => IncrementalClusterer.TrySplit(items, threshold, 0.9, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => IncrementalClusterer.TrySplit(items, 0.9, threshold, 1));
    }

    [Fact]
    public void The_check_threshold_may_not_exceed_the_join_threshold()
    {
        ClusterState[] clusters = [new(1, Unit(1, 0), 1)];

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            IncrementalClusterer.ClusterToCheck(Unit(1, 0), clusters, new HashSet<long>(), joinSimilarity: 0.6, checkSimilarity: 0.8, maxChecks: 1));
    }

    [Fact]
    public void Counts_and_limits_below_their_minimum_are_refused()
    {
        ClusterState[] clusters = [new(1, Unit(1, 0), 1)];
        EmbeddedItem[] items = [new(1, Unit(1, 0)), new(2, Unit(0, 1))];

        Assert.Throws<ArgumentException>(() => IncrementalClusterer.Assign([new ClusterState(1, Unit(1, 0), -1)], items, 0.8));
        Assert.Throws<ArgumentOutOfRangeException>(() => IncrementalClusterer.ClusterToCheck(Unit(1, 0), clusters, new HashSet<long>(), 0.9, 0.5, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => IncrementalClusterer.TrySplit(items, 0.9, 0.9, 0));
    }

    [Fact]
    public void Vectors_of_different_lengths_are_refused()
    {
        ClusterState[] clusters = [new(1, [1f, 0f], 1), new(2, [1f, 0f, 0f], 1)];

        Assert.Throws<ArgumentException>(() => IncrementalClusterer.FindMerges(clusters, 0.5));
        Assert.Throws<ArgumentException>(() => IncrementalClusterer.FindMerges(clusters, [(1, 2)], 0.5));
        Assert.Throws<ArgumentException>(() => IncrementalClusterer.Assign([clusters[0]], [new EmbeddedItem(1, [1f, 0f, 0f])], 0.5));
        Assert.Throws<ArgumentException>(() => IncrementalClusterer.Assign([], [new EmbeddedItem(1, [1f, 0f]), new EmbeddedItem(2, [1f, 0f, 0f])], 0.5));
        Assert.Throws<ArgumentException>(() => IncrementalClusterer.ClusterToCheck([1f, 0f, 0f], [clusters[0]], new HashSet<long>(), 0.9, 0.5, 1));
        Assert.Throws<ArgumentException>(() => IncrementalClusterer.Join(clusters[0], new EmbeddedItem(1, [1f, 0f, 0f])));
        Assert.Throws<ArgumentException>(() => IncrementalClusterer.Cohesion([new EmbeddedItem(1, [1f, 0f]), new EmbeddedItem(2, [1f])]));
    }

    [Fact]
    public void Duplicate_ids_are_refused()
    {
        ClusterState[] twins = [new(1, Unit(1, 0), 1), new(1, Unit(1, 0.1), 1)];
        EmbeddedItem[] sameItem = [new(7, Unit(1, 0)), new(7, Unit(1, 0.1))];

        Assert.Throws<ArgumentException>(() => IncrementalClusterer.Assign(twins, [new EmbeddedItem(9, Unit(1, 0))], 0.8));
        Assert.Throws<ArgumentException>(() => IncrementalClusterer.Assign([], sameItem, 0.8));
        Assert.Throws<ArgumentException>(() => IncrementalClusterer.FindMerges(twins, 0.5));
        Assert.Throws<ArgumentException>(() => IncrementalClusterer.FindMerges(twins, [(1, 1)], 0.5));
        Assert.Throws<ArgumentException>(() => IncrementalClusterer.TrySplit(sameItem, 0.9, 0.9, 1));
    }

    [Fact]
    public void A_missing_embedding_or_cluster_is_refused()
    {
        Assert.Throws<ArgumentException>(() => IncrementalClusterer.Assign([], [default], 0.8));
        Assert.Throws<ArgumentException>(() => IncrementalClusterer.Assign([null!], [], 0.8));
        Assert.Throws<ArgumentNullException>(() => IncrementalClusterer.Assign(null!, [], 0.8));
        Assert.Throws<ArgumentNullException>(() => IncrementalClusterer.Assign([], null!, 0.8));
        Assert.Throws<ArgumentNullException>(() => IncrementalClusterer.FindMerges([], null!, 0.8));
        Assert.Throws<ArgumentNullException>(() => IncrementalClusterer.ClusterToCheck(null!, [], new HashSet<long>(), 0.9, 0.5, 1));
        Assert.Throws<ArgumentNullException>(() => IncrementalClusterer.ClusterToCheck([1f], [], null!, 0.9, 0.5, 1));
        Assert.Throws<ArgumentNullException>(() => IncrementalClusterer.Join(null!, new EmbeddedItem(1, [1f])));
    }

    [Fact]
    public void A_centroid_that_is_not_unit_length_is_normalized_not_trusted()
    {
        // At cosine 0.45 the item is far from the cluster; a centroid of length 10 must not make it look close.
        ClusterState[] clusters = [new(1, [10f, 0f], 3)];
        var item = new EmbeddedItem(5, [1f, 2f]);

        var result = IncrementalClusterer.Assign(clusters, [item], 0.8);

        Assert.Empty(result.Joined);
        Assert.Null(IncrementalClusterer.ClusterToCheck(item.Embedding, clusters, new HashSet<long>(), 0.9, 0.5, 1));
    }

    [Fact]
    public void Identical_centroids_merge_the_same_way_whatever_order_the_pairs_come_in()
    {
        // Duplicate texts give identical centroids, so similarities tie exactly.
        ClusterState[] clusters = [new(1, Unit(1, 0), 2), new(2, Unit(1, 0), 2), new(3, Unit(1, 0), 2)];

        var all = IncrementalClusterer.FindMerges(clusters, 0.9);
        var forward = IncrementalClusterer.FindMerges(clusters, [(1, 2), (1, 3), (2, 3)], 0.9);
        var reversed = IncrementalClusterer.FindMerges(clusters, [(3, 2), (3, 1), (2, 1)], 0.9);

        var merge = Assert.Single(all);
        Assert.Equal(new MergeDecision(2, 1, merge.Similarity), merge);
        Assert.Equal(all, forward);
        Assert.Equal(all, reversed);
    }

    [Fact]
    public void A_zero_NaN_or_infinite_embedding_cannot_join_and_the_cluster_stays_usable()
    {
        var cluster = new ClusterState(1, Unit(1, 0), 3);

        Assert.Throws<ArgumentException>(() => IncrementalClusterer.Join(cluster, new EmbeddedItem(9, [float.NaN, 0f])));
        Assert.Throws<ArgumentException>(() => IncrementalClusterer.Join(cluster, new EmbeddedItem(9, [float.PositiveInfinity, 0f])));
        Assert.Throws<ArgumentException>(() => IncrementalClusterer.Join(cluster, new EmbeddedItem(9, [0f, 0f])));

        var result = IncrementalClusterer.Assign([cluster], [new EmbeddedItem(10, Unit(1, 0))], 0.9);
        Assert.Equal(1L, result.Joined[10]);
    }

    [Fact]
    public void A_cluster_with_a_NaN_member_stays_whole()
    {
        var nan = new float[] { float.NaN, 0f };
        EmbeddedItem[] members =
        [
            new(1, Unit(1, 0)), new(2, Unit(1, 0.05)), new(3, Unit(0, 1)), new(4, Unit(0.05, 1)), new(5, nan),
        ];

        Assert.True(double.IsNaN(IncrementalClusterer.Cohesion(members)));
        Assert.Null(IncrementalClusterer.TrySplit(members, cohesionThreshold: 0.9, regroupSimilarity: 0.9, minPartSize: 2));
    }

    [Fact]
    public void The_joined_map_cannot_be_changed_by_the_caller()
    {
        var result = IncrementalClusterer.Assign([new ClusterState(1, Unit(1, 0), 1)], [new EmbeddedItem(2, Unit(1, 0))], 0.8);

        Assert.IsNotType<Dictionary<long, long>>(result.Joined);
        Assert.Equal(1L, result.Joined[2]);
    }
}
