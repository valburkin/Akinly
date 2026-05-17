namespace Akinly;

/// <summary>
/// A leader clusterer (the "leader-follower" algorithm) over cosine similarity. Instead of
/// placing items in input order, it repeatedly:
/// <list type="number">
/// <item><description>picks as the next seed the unassigned item farthest from every
/// existing centroid (the first seed is the first item);</description></item>
/// <item><description>sweeps the unassigned items and adds to the new seed's cluster each
/// one within the similarity threshold of its centroid;</description></item>
/// </list>
/// until every item is assigned.
/// </summary>
/// <remarks>
/// <para>
/// Greedy clustering tends to grow one "magnet" cluster around whichever topic comes first:
/// its centroid drifts as borderline items join, which pulls in more loosely related ones,
/// while smaller real topics are left as fragments below the minimum size. Seeding from the
/// extremes of the embedding space first avoids that: a borderline item can only join the
/// seed it is actually close to, because the other seeds already exist.
/// </para>
/// <para>
/// The seed search costs O(n²) in the worst case, against greedy's O(n × k); negligible at a
/// few thousand items.
/// </para>
/// </remarks>
public sealed class LeaderCosineClusterer : IClusterer
{
    /// <inheritdoc />
    public IReadOnlyList<ItemCluster<TId>> Cluster<TId>(
        IReadOnlyList<(TId ItemId, float[] Embedding)> items,
        int minClusterSize,
        double similarityThreshold = 0.85)
        where TId : notnull
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            return [];
        }

        var normalized = items.Select(i => (i.ItemId, Vector: VectorMath.Normalize(i.Embedding))).ToArray();
        var assigned = new bool[normalized.Length];
        var centroids = new List<float[]>();
        var members = new List<List<TId>>();

        while (true)
        {
            var seed = PickNextSeed(normalized, assigned, centroids);
            if (seed < 0)
            {
                break;
            }

            // The centroid starts as a copy of the seed and moves as followers join.
            var centroid = (float[])normalized[seed].Vector.Clone();
            var followers = new List<TId> { normalized[seed].ItemId };
            assigned[seed] = true;

            for (var i = 0; i < normalized.Length; i++)
            {
                if (assigned[i] || VectorMath.Dot(normalized[i].Vector, centroid) < similarityThreshold)
                {
                    continue;
                }

                followers.Add(normalized[i].ItemId);
                assigned[i] = true;
                VectorMath.AddToMean(centroid, normalized[i].Vector, followers.Count);
            }

            centroids.Add(centroid);
            members.Add(followers);
        }

        return ClusterSample.Build(normalized, centroids, members, minClusterSize);
    }

    /// <summary>
    /// The unassigned item farthest from every centroid (the lowest best similarity to any of
    /// them), the first unassigned item when there are no centroids yet, or -1 when every
    /// item is assigned.
    /// </summary>
    private static int PickNextSeed<TId>((TId ItemId, float[] Vector)[] normalized, bool[] assigned, List<float[]> centroids)
    {
        var bestIndex = -1;
        var lowestSimilarity = double.PositiveInfinity;
        for (var i = 0; i < normalized.Length; i++)
        {
            if (assigned[i])
            {
                continue;
            }

            if (centroids.Count == 0)
            {
                return i;
            }

            var nearest = centroids.Max(c => VectorMath.Dot(normalized[i].Vector, c));
            if (nearest < lowestSimilarity)
            {
                lowestSimilarity = nearest;
                bestIndex = i;
            }
        }

        return bestIndex;
    }
}
