namespace Akinly;

/// <summary>
/// A greedy cosine-similarity clusterer. Each item joins the existing cluster whose
/// centroid is most similar, if that similarity reaches the threshold; otherwise it
/// starts a new cluster. Centroids are running means of L2-normalized vectors.
/// </summary>
/// <remarks>
/// One pass, O(n × k) for n items and k clusters: good enough for a few thousand items.
/// The result depends on the input order, and a cluster that forms early around a
/// dominant topic can drift toward its neighbors as borderline items join it; see
/// <see cref="LeaderCosineClusterer"/>.
/// </remarks>
public sealed class GreedyCosineClusterer : IClusterer
{
    /// <inheritdoc />
    public IReadOnlyList<ItemCluster> Cluster(
        IReadOnlyList<(long ItemId, float[] Embedding)> items,
        int minClusterSize,
        double similarityThreshold = 0.85)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            return [];
        }

        var centroids = new List<float[]>();
        var members = new List<List<long>>();
        var normalized = items.Select(i => (i.ItemId, Vector: VectorMath.Normalize(i.Embedding))).ToArray();

        foreach (var (itemId, vector) in normalized)
        {
            var bestIndex = -1;
            var bestSimilarity = double.NegativeInfinity;
            for (var i = 0; i < centroids.Count; i++)
            {
                var similarity = VectorMath.Dot(vector, centroids[i]);
                if (similarity > bestSimilarity)
                {
                    bestSimilarity = similarity;
                    bestIndex = i;
                }
            }

            if (bestIndex >= 0 && bestSimilarity >= similarityThreshold)
            {
                members[bestIndex].Add(itemId);
                VectorMath.AddToMean(centroids[bestIndex], vector, members[bestIndex].Count);
            }
            else
            {
                centroids.Add((float[])vector.Clone());
                members.Add([itemId]);
            }
        }

        return ClusterSample.Build(normalized, centroids, members, minClusterSize);
    }
}
