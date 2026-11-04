namespace Akinly;

/// <summary>
/// Incremental clustering with lasting cluster ids: new items are placed against clusters
/// that already exist, of any age, so an item that comes back after months joins the cluster
/// it had before; periodic merge and split checks repair what placement in arrival order gets
/// wrong. Pure functions: the caller loads clusters and stores the results.
/// </summary>
public static class IncrementalClusterer
{
    /// <summary>
    /// Places each item in the most similar cluster at or above
    /// <paramref name="joinSimilarity"/>, or starts a new one. Clusters created in this call
    /// take later items too. Centroids are running means, renormalized. Deterministic for a
    /// given input order.
    /// </summary>
    /// <param name="existing">The clusters that already exist.</param>
    /// <param name="items">The items to place, in order.</param>
    /// <param name="joinSimilarity">The cosine similarity at or above which an item joins a cluster.</param>
    public static AssignmentResult Assign(
        IReadOnlyList<ClusterState> existing, IReadOnlyList<EmbeddedItem> items, double joinSimilarity)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(items);

        // The working set: existing clusters first (index < existing.Count), then new ones.
        var centroids = existing.Select(c => (float[])c.Centroid.Clone()).ToList();
        var counts = existing.Select(c => c.Count).ToList();
        var newMembers = new List<List<long>>();
        var joined = new Dictionary<long, long>();
        var touched = new HashSet<int>();

        foreach (var item in items)
        {
            var vector = Vectors.Normalize(item.Embedding);
            var best = -1;
            var bestSimilarity = double.NegativeInfinity;
            for (var i = 0; i < centroids.Count; i++)
            {
                var similarity = Vectors.Dot(vector, centroids[i]);
                if (similarity > bestSimilarity)
                {
                    bestSimilarity = similarity;
                    best = i;
                }
            }

            if (best >= 0 && bestSimilarity >= joinSimilarity)
            {
                counts[best]++;
                Vectors.AddToMean(centroids[best], vector, counts[best]);
                if (best < existing.Count)
                {
                    joined[item.Id] = existing[best].Id;
                    touched.Add(best);
                }
                else
                {
                    newMembers[best - existing.Count].Add(item.Id);
                }
            }
            else
            {
                centroids.Add((float[])vector.Clone());
                counts.Add(1);
                newMembers.Add([item.Id]);
            }
        }

        var created = newMembers.Select((members, i) => new NewCluster(centroids[existing.Count + i], members)).ToList();
        var updated = touched.Order().Select(i => existing[i] with { Centroid = centroids[i], Count = counts[i] }).ToList();
        return new AssignmentResult(joined, created, updated);
    }

    /// <summary>
    /// The pairs of clusters close enough to be one, most similar first. Each cluster merges
    /// at most once per call, into the larger of the pair (on a tie, the one with the lower id).
    /// </summary>
    /// <param name="clusters">The clusters to compare, every pair of them.</param>
    /// <param name="mergeSimilarity">The cosine similarity of two centroids at or above which they merge.</param>
    public static IReadOnlyList<MergeDecision> FindMerges(IReadOnlyList<ClusterState> clusters, double mergeSimilarity)
    {
        ArgumentNullException.ThrowIfNull(clusters);
        var candidates = new List<MergeDecision>();
        for (var i = 0; i < clusters.Count; i++)
        {
            for (var j = i + 1; j < clusters.Count; j++)
            {
                var similarity = Vectors.Dot(clusters[i].Centroid, clusters[j].Centroid);
                if (similarity < mergeSimilarity)
                {
                    continue;
                }

                var (into, from) = Larger(clusters[i], clusters[j]);
                candidates.Add(new MergeDecision(from.Id, into.Id, similarity));
            }
        }

        var used = new HashSet<long>();
        var merges = new List<MergeDecision>();
        foreach (var merge in candidates.OrderByDescending(m => m.Similarity).ThenBy(m => m.FromId))
        {
            if (used.Contains(merge.FromId) || used.Contains(merge.IntoId))
            {
                continue;
            }

            used.Add(merge.FromId);
            used.Add(merge.IntoId);
            merges.Add(merge);
        }

        return merges;
    }

    /// <summary>
    /// The mean similarity of the members to their centroid: 1 when they all point the same way.
    /// </summary>
    public static double Cohesion(IReadOnlyList<EmbeddedItem> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        if (members.Count == 0)
        {
            return 1;
        }

        var normalized = members.Select(m => Vectors.Normalize(m.Embedding)).ToList();
        var centroid = Vectors.Mean(normalized);
        return normalized.Average(v => Vectors.Dot(v, centroid));
    }

    /// <summary>
    /// Splits a loose cluster: when its <see cref="Cohesion"/> is below
    /// <paramref name="cohesionThreshold"/>, regroups its members at
    /// <paramref name="regroupSimilarity"/>. Returns the parts, largest first (the caller keeps
    /// the cluster's id on it), when at least two parts have <paramref name="minPartSize"/>
    /// members; members of smaller parts stay with the largest. Null when the cluster should
    /// stay whole.
    /// </summary>
    public static IReadOnlyList<NewCluster>? TrySplit(
        IReadOnlyList<EmbeddedItem> members, double cohesionThreshold, double regroupSimilarity, int minPartSize)
    {
        ArgumentNullException.ThrowIfNull(members);
        if (members.Count < 2 * minPartSize || Cohesion(members) >= cohesionThreshold)
        {
            return null;
        }

        var regrouped = Assign([], members, regroupSimilarity).Created;
        var parts = regrouped.Where(p => p.ItemIds.Count >= minPartSize).OrderByDescending(p => p.ItemIds.Count).ToList();
        if (parts.Count < 2)
        {
            return null;
        }

        var stragglers = regrouped.Where(p => p.ItemIds.Count < minPartSize).SelectMany(p => p.ItemIds).ToList();
        var byId = members.ToDictionary(m => m.Id);
        var result = new List<NewCluster>();
        for (var i = 0; i < parts.Count; i++)
        {
            List<long> ids = i == 0 ? [.. parts[i].ItemIds, .. stragglers] : [.. parts[i].ItemIds];
            var centroid = Vectors.Mean([.. ids.Select(id => Vectors.Normalize(byId[id].Embedding))]);
            result.Add(new NewCluster(centroid, ids));
        }

        return result;
    }

    private static (ClusterState Into, ClusterState From) Larger(ClusterState a, ClusterState b) =>
        a.Count > b.Count || (a.Count == b.Count && a.Id < b.Id) ? (a, b) : (b, a);
}
