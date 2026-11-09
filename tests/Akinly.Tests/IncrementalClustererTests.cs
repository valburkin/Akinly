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

    [Fact]
    public void Members_of_parts_too_small_to_stand_alone_stay_with_the_largest_part()
    {
        var members = Enumerable.Range(0, 6).Select(i => new EmbeddedItem(i, Axis(0, 0.05f, 10 + i)))
            .Concat(Enumerable.Range(10, 4).Select(i => new EmbeddedItem(i, Axis(1, 0.05f, i))))
            .Append(new EmbeddedItem(99, Axis(2)))
            .ToList();

        var parts = IncrementalClusterer.TrySplit(members, cohesionThreshold: 0.8, regroupSimilarity: 0.85, minPartSize: 3);

        Assert.NotNull(parts);
        Assert.Equal([7, 4], parts.Select(p => p.ItemIds.Count));
        Assert.Contains(99L, parts[0].ItemIds);
        Assert.Equal(members.Count, parts.Sum(p => p.ItemIds.Count));
    }

    [Fact]
    public void A_cluster_with_no_members_left_takes_its_next_member_as_its_centroid()
    {
        var item = Axis(1, 0.1f, 11);

        var result = IncrementalClusterer.Assign([new ClusterState(7, Vectors.Normalize(item), 0)], [new EmbeddedItem(1, item)], 0.8);

        var updated = Assert.Single(result.Updated);
        Assert.Equal(1, updated.Count);
        Assert.Equal(1.0, Vectors.Dot(updated.Centroid, Vectors.Normalize(item)), 6);
    }

    [Fact]
    public void Zero_and_NaN_vectors_never_join_and_never_attract()
    {
        var nan = Axis(0);
        nan[3] = float.NaN;
        var existing = new[] { new ClusterState(100, Axis(0), 5) };

        var result = IncrementalClusterer.Assign(
            existing,
            [
                new EmbeddedItem(1, new float[16]),
                new EmbeddedItem(2, nan),
                new EmbeddedItem(3, Axis(0, 0.1f, 11)),
            ],
            joinSimilarity: 0.5);

        Assert.Equal([3L], result.Joined.Keys);
        Assert.Equal(100, result.Joined[3]);
        Assert.Equal([1L, 2L], result.Created.Select(c => Assert.Single(c.ItemIds)));

        // A broken centroid is never close enough to merge.
        Assert.Empty(IncrementalClusterer.FindMerges([new ClusterState(1, nan, 3), new ClusterState(2, Axis(0), 3)], 0.5));
    }

    [Fact]
    public void Cohesion_of_no_members_is_whole() =>
        Assert.Equal(1.0, IncrementalClusterer.Cohesion([]));

    [Fact]
    public void Merges_over_given_pairs_match_the_full_check_when_the_pairs_hold_the_close_ones()
    {
        var clusters = new[]
        {
            new ClusterState(1, Axis(0), 3),
            new ClusterState(2, Vectors.Normalize(Axis(0, 0.1f)), 9),
            new ClusterState(3, Vectors.Normalize(Axis(0, 0.2f)), 4),
            new ClusterState(4, Axis(5), 50),
        };
        var all = IncrementalClusterer.FindMerges(clusters, 0.95);

        // Each changed cluster with its nearest neighbors, both ways, a cluster with itself,
        // and a cluster that is not there.
        var paired = IncrementalClusterer.FindMerges(clusters, [(1, 2), (2, 1), (3, 2), (1, 3), (2, 2), (3, 404), (4, 1)], 0.95);

        Assert.Equal(all, paired);
        Assert.Empty(IncrementalClusterer.FindMerges(clusters, [(1, 4), (3, 4)], 0.95));
    }

    /// <summary>A unit vector in the plane at (<paramref name="a"/>, <paramref name="b"/>).</summary>
    private static float[] Unit(double a, double b) => Vectors.Normalize([(float)a, (float)b]);

    [Fact]
    public void An_item_near_a_cluster_is_checked_against_it()
    {
        ClusterState[] clusters = [new(1, Unit(1, 0), 3), new(2, Unit(0, 1), 3)];

        Assert.Equal(1L, IncrementalClusterer.ClusterToCheck(Unit(1, 0.8), clusters, new HashSet<long>(), 0.85, 0.62, 2));
    }

    [Fact]
    public void An_item_certainly_in_a_cluster_needs_no_check()
    {
        ClusterState[] clusters = [new(1, Unit(1, 0), 3)];

        Assert.Null(IncrementalClusterer.ClusterToCheck(Unit(1, 0.1), clusters, new HashSet<long>(), 0.85, 0.62, 2));
    }

    [Fact]
    public void An_item_far_from_every_cluster_needs_no_check()
    {
        ClusterState[] clusters = [new(1, Unit(1, 0), 3)];

        Assert.Null(IncrementalClusterer.ClusterToCheck(Unit(0.1, 1), clusters, new HashSet<long>(), 0.85, 0.62, 2));
    }

    [Fact]
    public void A_refused_item_is_checked_against_the_next_nearest_then_starts_its_own()
    {
        ClusterState[] clusters = [new(1, Unit(1, 0), 3), new(2, Unit(0.9, 1), 3)];
        var item = Unit(1, 0.7);

        Assert.Equal(2L, IncrementalClusterer.ClusterToCheck(item, clusters, new HashSet<long> { 1 }, 0.99, 0.62, 2));
        Assert.Null(IncrementalClusterer.ClusterToCheck(item, clusters, new HashSet<long> { 1, 2 }, 0.99, 0.62, 2));
    }

    [Fact]
    public void Assign_skips_a_cluster_that_refused_the_item()
    {
        ClusterState[] clusters = [new(1, Unit(1, 0), 3)];

        var result = IncrementalClusterer.Assign(clusters, [new EmbeddedItem(9, Unit(1, 0.1))], 0.8, (item, cluster) => item == 9 && cluster == 1);

        Assert.Empty(result.Joined);
        Assert.Single(result.Created);
    }
}
