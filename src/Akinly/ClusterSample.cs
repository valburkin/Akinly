namespace Akinly;

/// <summary>Builds the clusters a clusterer returns, with their diverse member samples.</summary>
internal static class ClusterSample
{
    private const int SampleEachEnd = 5;

    /// <summary>
    /// The clusters with at least <paramref name="minClusterSize"/> members, numbered from zero,
    /// each with its sample: the members nearest the centroid show what the cluster is about,
    /// and the farthest ones still in it show how far it strays, so a reader (or a labeling
    /// model) can tell a coherent cluster from a loose one.
    /// </summary>
    public static List<ItemCluster<TId>> Build<TId>(
        (TId ItemId, float[] Vector)[] normalized,
        List<float[]> centroids,
        List<List<TId>> members,
        int minClusterSize)
        where TId : notnull
    {
        var clusters = new List<ItemCluster<TId>>();
        for (var i = 0; i < centroids.Count; i++)
        {
            if (members[i].Count < minClusterSize)
            {
                continue;
            }

            var memberSet = members[i].ToHashSet();
            var byProximity = normalized
                .Where(n => memberSet.Contains(n.ItemId))
                .Select(n => (n.ItemId, Similarity: VectorMath.Dot(n.Vector, centroids[i])))
                .OrderByDescending(n => n.Similarity)
                .Select(n => n.ItemId)
                .ToArray();
            var nearest = byProximity.Take(SampleEachEnd);
            var farthest = byProximity.Reverse().Take(SampleEachEnd);
            var sample = nearest.Concat(farthest).Distinct().ToArray();

            clusters.Add(new ItemCluster<TId>(clusters.Count, centroids[i], members[i], sample));
        }

        return clusters;
    }
}
