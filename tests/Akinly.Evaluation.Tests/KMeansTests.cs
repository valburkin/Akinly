namespace Akinly.Evaluation.Tests;

public sealed class KMeansTests
{
    [Fact]
    public void Separated_groups_are_found_and_the_seed_makes_it_repeatable()
    {
        var random = new Random(4);
        var vectors = Enumerable.Range(0, 60)
            .Select(i => new[] { i % 3 == 0 ? 1f : 0.05f, i % 3 == 1 ? 1f : 0.05f, i % 3 == 2 ? 1f : 0.05f, (float)(random.NextDouble() * 0.1) })
            .ToList();

        var assigned = KMeans.Cluster(vectors, 3, seed: 7);

        var scores = Metrics.Score([.. Enumerable.Range(0, 60).Select(i => (i % 3).ToString(System.Globalization.CultureInfo.InvariantCulture))], [.. assigned.Select(a => (long)a)]);
        Assert.Equal(1.0, scores.Accuracy, 9);
        Assert.Equal(assigned, KMeans.Cluster(vectors, 3, seed: 7));
        Assert.Equal(2, KMeans.Cluster(vectors.Take(2).ToList(), 5, seed: 1).Length);
    }
}
