using System.Numerics.Tensors;

namespace Akinly.Evaluation;

/// <summary>
/// Spherical k-means (cosine similarity to unit-length centers), seeded with k-means++: the
/// reference row of an evaluation, told the true number of groups and given every text at once.
/// </summary>
public static class KMeans
{
    /// <summary>Assigns each vector to one of <paramref name="k"/> clusters.</summary>
    /// <returns>The cluster index of each vector, in input order.</returns>
    public static int[] Cluster(IReadOnlyList<float[]> vectors, int k, int seed, int maxIterations = 100)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(k, 1);
        var points = vectors.Select(Vectors.Normalize).ToArray();
        k = Math.Min(k, points.Length);
        var random = new Random(seed);
        var centers = Seed(points, k, random);
        var assigned = new int[points.Length];
        Array.Fill(assigned, -1);
        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            var moved = 0;
            for (var i = 0; i < points.Length; i++)
            {
                var best = Nearest(points[i], centers);
                moved += best == assigned[i] ? 0 : 1;
                assigned[i] = best;
            }

            if (moved == 0)
            {
                break;
            }

            // New centers: each cluster's mean direction; an emptied one restarts at a random point.
            var sums = new double[k][];
            for (var c = 0; c < k; c++)
            {
                sums[c] = new double[points[0].Length];
            }

            for (var i = 0; i < points.Length; i++)
            {
                var sum = sums[assigned[i]];
                for (var d = 0; d < sum.Length; d++)
                {
                    sum[d] += points[i][d];
                }
            }

            for (var c = 0; c < k; c++)
            {
                centers[c] = sums[c].All(v => v == 0)
                    ? points[random.Next(points.Length)]
                    : Vectors.Normalize([.. sums[c].Select(v => (float)v)]);
            }
        }

        return assigned;
    }

    /// <summary>k-means++: each next center is drawn with probability growing with its distance to the nearest center so far.</summary>
    private static float[][] Seed(float[][] points, int k, Random random)
    {
        var centers = new List<float[]> { points[random.Next(points.Length)] };
        var distance = points.Select(p => 1 - TensorPrimitives.Dot(p, centers[0])).Select(d => Math.Max(0, (double)d)).ToArray();
        while (centers.Count < k)
        {
            var total = distance.Sum();
            var pick = random.NextDouble() * total;
            var index = 0;
            for (var cumulative = distance[0]; cumulative < pick && index < points.Length - 1; cumulative += distance[++index])
            {
            }

            centers.Add(points[index]);
            for (var i = 0; i < points.Length; i++)
            {
                distance[i] = Math.Min(distance[i], Math.Max(0, 1 - (double)TensorPrimitives.Dot(points[i], points[index])));
            }
        }

        return [.. centers];
    }

    private static int Nearest(float[] point, float[][] centers)
    {
        var (best, similarity) = (0, float.NegativeInfinity);
        for (var c = 0; c < centers.Length; c++)
        {
            var s = TensorPrimitives.Dot(point, centers[c]);
            if (s > similarity)
            {
                (best, similarity) = (c, s);
            }
        }

        return best;
    }
}
