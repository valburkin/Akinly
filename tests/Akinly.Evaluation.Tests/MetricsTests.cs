namespace Akinly.Evaluation.Tests;

/// <summary>The measures against scikit-learn's values for the same inputs.</summary>
public sealed class MetricsTests
{
    [Fact]
    public void A_perfect_clustering_scores_one_everywhere()
    {
        var scores = Metrics.Score(["a", "a", "b", "b", "c"], [7, 7, 3, 3, 9]);

        Assert.Equal(1.0, scores.AdjustedRand, 9);
        Assert.Equal(1.0, scores.Nmi, 9);
        Assert.Equal(1.0, scores.VMeasure, 9);
        Assert.Equal((1.0, 1.0, 1.0), (scores.PairPrecision, scores.PairRecall, scores.PairF1));
        Assert.Equal((3, 1), (scores.Clusters, scores.Singletons));
    }

    [Fact]
    public void The_user_guide_example_matches_scikit_learn()
    {
        // labels_true = [0, 0, 0, 1, 1, 1], labels_pred = [0, 0, 1, 1, 2, 2], from scikit-learn's user guide.
        var scores = Metrics.Score(["0", "0", "0", "1", "1", "1"], [0, 0, 1, 1, 2, 2]);

        Assert.Equal(0.24242424, scores.AdjustedRand, 7);
        Assert.Equal(0.66666667, scores.Homogeneity, 7);
        Assert.Equal(0.42061983, scores.Completeness, 7);
        Assert.Equal(0.51580374, scores.VMeasure, 7);
        Assert.Equal(scores.VMeasure, scores.Nmi, 12);   // arithmetic NMI equals V-measure
        Assert.Equal((2.0 / 3, 2.0 / 6), (scores.PairPrecision, scores.PairRecall));

        // Matching each label to one cluster: 0 with cluster 0 (2 items), 1 with cluster 2 (2 items).
        Assert.Equal(4.0 / 6, scores.Accuracy, 9);
    }

    [Fact]
    public void Accuracy_matches_labels_to_clusters_one_to_one_even_with_more_clusters_than_labels()
    {
        // Two labels, four clusters: the best one-to-one matching covers 3 + 2 of 8 items.
        var scores = Metrics.Score(["a", "a", "a", "a", "b", "b", "b", "b"], [1, 1, 1, 2, 3, 3, 4, 4]);

        Assert.Equal(5.0 / 8, scores.Accuracy, 9);
        Assert.Equal(1.0, Metrics.Score(["x", "y", "y"], [7, 3, 3]).Accuracy, 9);
        Assert.Equal(1.0, Metrics.Score([], []).Accuracy, 9);
    }

    [Fact]
    public void A_split_group_matches_the_reference_values()
    {
        // adjusted_rand_score([0,0,1,1],[0,0,1,2]) = 0.5714285714; homogeneity 1, completeness 2/3, V-measure 0.8.
        var scores = Metrics.Score(["x", "x", "y", "y"], [1, 1, 2, 3]);

        Assert.Equal(0.5714285714, scores.AdjustedRand, 9);
        Assert.Equal(1.0, scores.Homogeneity, 9);
        Assert.Equal(2.0 / 3, scores.Completeness, 9);
        Assert.Equal(0.8, scores.VMeasure, 9);
        Assert.Equal((1.0, 0.5), (scores.PairPrecision, scores.PairRecall));
    }

    [Fact]
    public void Everything_in_one_cluster_scores_zero_on_adjusted_rand_and_nmi()
    {
        var scores = Metrics.Score(["a", "a", "b", "b", "c", "c"], [1, 1, 1, 1, 1, 1]);

        Assert.Equal(0.0, scores.AdjustedRand, 9);
        Assert.Equal(0.0, scores.Nmi, 9);
        Assert.Equal(1.0, scores.Completeness, 9);
        Assert.Equal((0.2, 1.0), (scores.PairPrecision, scores.PairRecall));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Fewer_than_two_items_are_a_perfect_match_as_in_scikit_learn(int count)
    {
        var scores = Metrics.Score([.. Enumerable.Repeat("a", count)], [.. Enumerable.Repeat(1L, count)]);

        Assert.Equal((1.0, 1.0), (scores.AdjustedRand, scores.Nmi));
    }

    [Fact]
    public void All_apart_on_both_sides_is_a_perfect_match()
    {
        var scores = Metrics.Score(["a", "b", "c"], [1, 2, 3]);

        Assert.Equal((1.0, 1.0), (scores.AdjustedRand, scores.Nmi));
    }

    [Fact]
    public void Mismatched_lengths_are_refused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Metrics.Score(["a"], [1, 2]));
}
