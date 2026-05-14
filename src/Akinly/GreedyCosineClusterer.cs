namespace Akinly;

/// <summary>
/// A greedy cosine-similarity clusterer. Each item joins the existing cluster whose
/// centroid is most similar, if that similarity reaches the threshold; otherwise it
/// starts a new cluster. Centroids are running means of L2-normalized vectors.
/// </summary>
/// <remarks>
/// One pass, O(n × k) for n items and k clusters: good enough for a few thousand items.
/// The result depends on the input order.
/// </remarks>
public sealed class GreedyCosineClusterer : IClusterer
{
    private const int SampleSize = 10;

    /// <inheritdoc />
    public IReadOnlyList<ItemCluster> Cluster(
        IReadOnlyList<(long ItemId, float[] Embedding)> items,
        int minClusterSize,
        double similarityThreshold = 0.78)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            return [];
        }

        var centroids = new List<float[]>();
        var members = new List<List<long>>();
        var normalized = items.Select(i => (i.ItemId, Vector: Normalize(i.Embedding))).ToArray();

        foreach (var (itemId, vector) in normalized)
        {
            var bestIndex = -1;
            var bestSimilarity = double.NegativeInfinity;
            for (var i = 0; i < centroids.Count; i++)
            {
                var similarity = Dot(vector, centroids[i]);
                if (similarity > bestSimilarity)
                {
                    bestSimilarity = similarity;
                    bestIndex = i;
                }
            }

            if (bestIndex >= 0 && bestSimilarity >= similarityThreshold)
            {
                members[bestIndex].Add(itemId);
                UpdateCentroid(centroids[bestIndex], vector, members[bestIndex].Count);
            }
            else
            {
                centroids.Add((float[])vector.Clone());
                members.Add([itemId]);
            }
        }

        var clusters = new List<ItemCluster>();
        for (var i = 0; i < centroids.Count; i++)
        {
            if (members[i].Count < minClusterSize)
            {
                continue;
            }

            var memberSet = members[i].ToHashSet();
            var nearest = normalized
                .Where(n => memberSet.Contains(n.ItemId))
                .Select(n => (n.ItemId, Similarity: Dot(n.Vector, centroids[i])))
                .OrderByDescending(n => n.Similarity)
                .Take(SampleSize)
                .Select(n => n.ItemId)
                .ToArray();

            clusters.Add(new ItemCluster(clusters.Count, centroids[i], members[i], nearest));
        }

        return clusters;
    }

    private static float[] Normalize(float[] vector)
    {
        var sum = 0.0;
        for (var i = 0; i < vector.Length; i++)
        {
            sum += vector[i] * vector[i];
        }

        var norm = Math.Sqrt(sum);
        if (norm < 1e-9)
        {
            return vector;
        }

        var copy = new float[vector.Length];
        for (var i = 0; i < vector.Length; i++)
        {
            copy[i] = (float)(vector[i] / norm);
        }

        return copy;
    }

    private static double Dot(float[] a, float[] b)
    {
        var sum = 0.0;
        for (var i = 0; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }

    private static void UpdateCentroid(float[] centroid, float[] sample, int newCount)
    {
        for (var i = 0; i < centroid.Length; i++)
        {
            centroid[i] = ((centroid[i] * (newCount - 1)) + sample[i]) / newCount;
        }

        // Renormalize, so cosine similarity stays a dot product on the unit sphere.
        var sum = 0.0;
        for (var i = 0; i < centroid.Length; i++)
        {
            sum += centroid[i] * centroid[i];
        }

        var norm = Math.Sqrt(sum);
        if (norm < 1e-9)
        {
            return;
        }

        for (var i = 0; i < centroid.Length; i++)
        {
            centroid[i] = (float)(centroid[i] / norm);
        }
    }
}
