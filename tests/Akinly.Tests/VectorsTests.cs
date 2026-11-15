namespace Akinly.Tests;

public sealed class VectorsTests
{
    private static double Length(float[] v) => Math.Sqrt(Vectors.Dot(v, v));

    [Fact]
    public void Normalize_returns_a_unit_length_copy()
    {
        float[] v = [3f, 4f];

        var unit = Vectors.Normalize(v);

        Assert.Equal([0.6f, 0.8f], unit);
        Assert.Equal([3f, 4f], v);
    }

    [Fact]
    public void A_zero_vector_normalizes_to_a_zero_copy()
    {
        float[] zero = [0f, 0f];

        var normalized = Vectors.Normalize(zero);

        Assert.Equal([0f, 0f], normalized);
        Assert.NotSame(zero, normalized);
    }

    [Fact]
    public void The_mean_of_no_vectors_is_empty() =>
        Assert.Empty(Vectors.Mean([]));

    [Fact]
    public void The_mean_points_between_its_vectors_and_is_unit_length()
    {
        var mean = Vectors.Mean([[1f, 0f], [0f, 1f]]);

        Assert.Equal(1.0, Length(mean), 6);
        Assert.Equal(mean[0], mean[1], 6);
    }

    [Fact]
    public void The_weighted_mean_leans_toward_the_larger_cluster()
    {
        var merged = Vectors.WeightedMean([1f, 0f], 9, [0f, 1f], 1);

        Assert.Equal(1.0, Length(merged), 6);
        Assert.True(Vectors.Dot(merged, [1f, 0f]) > Vectors.Dot(merged, [0f, 1f]));
        Assert.Equal(9.0, merged[0] / merged[1], 4);
    }

    /// <remarks>
    /// The running mean is renormalized after each member, which reweights the members a
    /// little, so it only approximates the mean of all members: close in direction, not equal.
    /// </remarks>
    [Fact]
    public void Adding_to_a_mean_stays_close_to_the_mean_of_all_members()
    {
        float[] a = Vectors.Normalize([1f, 0.2f, 0f]);
        float[] b = Vectors.Normalize([0.3f, 1f, 0f]);
        float[] c = Vectors.Normalize([0f, 0.4f, 1f]);

        var running = (float[])a.Clone();
        Vectors.AddToMean(running, b, 2);
        Vectors.AddToMean(running, c, 3);

        Assert.InRange(Vectors.Dot(running, Vectors.Mean([a, b, c])), 0.99, 1.0 + 1e-6);
    }

    [Fact]
    public void Adding_the_opposite_of_a_lone_member_leaves_a_zero_centroid_unscaled()
    {
        float[] centroid = [1f, 0f];

        Vectors.AddToMean(centroid, [-1f, 0f], 2);

        Assert.Equal([0f, 0f], centroid);
    }

    [Fact]
    public void Large_components_do_not_overflow_and_only_vectors_shorter_than_1e9_count_as_zero()
    {
        Assert.Equal([1f, 0f], Vectors.Normalize([1e20f, 0f]));
        var small = Vectors.Normalize([3e-8f, 4e-8f]);
        Assert.Equal(0.6, small[0], 6);
        Assert.Equal(0.8, small[1], 6);
        Assert.Equal([0f, 0f], Vectors.Normalize([3e-25f, 4e-25f]));
    }

    [Fact]
    public void Vectors_of_different_lengths_are_refused()
    {
        Assert.Throws<ArgumentException>(() => Vectors.Dot([1f, 0f], [1f, 0f, 5f]));
        Assert.Throws<ArgumentException>(() => Vectors.Mean([[1f, 0f], [1f]]));
        Assert.Throws<ArgumentException>(() => Vectors.WeightedMean([1f, 0f], 1, [1f], 1));
        Assert.Throws<ArgumentException>(() => Vectors.AddToMean([1f, 0f], [1f], 2));
        Assert.Throws<ArgumentException>(() => Vectors.Mean([[1f], null!]));
    }

    [Fact]
    public void Counts_below_their_minimum_are_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Vectors.WeightedMean([1f], -1, [1f], 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Vectors.WeightedMean([1f], 1, [1f], -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Vectors.AddToMean([1f], [1f], 0));
    }

    [Fact]
    public void Null_vectors_are_refused()
    {
        Assert.Throws<ArgumentNullException>(() => Vectors.Normalize(null!));
        Assert.Throws<ArgumentNullException>(() => Vectors.Dot(null!, []));
        Assert.Throws<ArgumentNullException>(() => Vectors.Dot([], null!));
        Assert.Throws<ArgumentNullException>(() => Vectors.Mean(null!));
        Assert.Throws<ArgumentNullException>(() => Vectors.WeightedMean(null!, 1, [], 1));
        Assert.Throws<ArgumentNullException>(() => Vectors.WeightedMean([], 1, null!, 1));
        Assert.Throws<ArgumentNullException>(() => Vectors.AddToMean(null!, [], 1));
        Assert.Throws<ArgumentNullException>(() => Vectors.AddToMean([], null!, 1));
    }
}
