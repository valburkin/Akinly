namespace Akinly;

/// <summary>
/// Incremental clustering with lasting cluster ids: a greedy join-nearest-or-create loop over
/// cosine similarity, applied to new items against clusters that already exist, of any age,
/// so an item that comes back after months joins the cluster it had before; periodic merge
/// and split checks repair what placement in arrival order gets wrong.
/// </summary>
/// <remarks>
/// <para>
/// Pure functions: the caller loads clusters and stores the results, and may hand in only the
/// clusters (or pairs) that can matter, such as each item's nearest clusters from a vector
/// index, instead of every cluster. The same input in the same order always gives the same
/// result.
/// </para>
/// <para>
/// Every embedding and centroid in one call must have the same length; centroids need not be
/// unit length (they are normalized on the way in). Similarity thresholds are cosine
/// similarities above 0 and at most 1. Zero vectors and vectors holding a NaN never join a
/// cluster, never attract an item and never merge.
/// </para>
/// </remarks>
public static class IncrementalClusterer
{
    /// <summary>
    /// Places each item in the most similar cluster at or above
    /// <paramref name="joinSimilarity"/>, or starts a new one. Clusters created in this call
    /// take later items too. Centroids are running means, renormalized.
    /// </summary>
    /// <param name="existing">The clusters that already exist, with unique ids.</param>
    /// <param name="items">The items to place, in order, with unique ids.</param>
    /// <param name="joinSimilarity">The cosine similarity at or above which an item joins a cluster.</param>
    /// <param name="refused">
    /// Whether an item may not join an existing cluster (item id, cluster id), because a judge
    /// already refused it there; such a cluster is skipped as if it were not there. Null when
    /// nothing was refused.
    /// </param>
    /// <returns>Where each item went, the clusters it formed, and the existing clusters that changed.</returns>
    /// <exception cref="ArgumentException">
    /// Duplicate ids, a missing vector, vectors of different lengths, or a negative count.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">The threshold is not above 0 and at most 1.</exception>
    public static AssignmentResult Assign(
        IReadOnlyList<ClusterState> existing,
        IReadOnlyList<EmbeddedItem> items,
        double joinSimilarity,
        Func<long, long, bool>? refused = null)
    {
        var dimensions = Guard.Clusters(existing, nameof(existing));
        Guard.Items(items, nameof(items), dimensions);
        Guard.Similarity(joinSimilarity, nameof(joinSimilarity));

        // The working set: existing clusters first (index < existing.Count), then new ones.
        var centroids = existing.Select(c => Vectors.Normalize(c.Centroid)).ToList();
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
                if (i < existing.Count && refused?.Invoke(item.Id, existing[i].Id) == true)
                {
                    continue;
                }

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
                centroids.Add(vector);
                counts.Add(1);
                newMembers.Add([item.Id]);
            }
        }

        var created = newMembers.Select((members, i) => new NewCluster(centroids[existing.Count + i], members)).ToList();
        var updated = touched.Order().Select(i => existing[i] with { Centroid = centroids[i], Count = counts[i] }).ToList();
        return new AssignmentResult(joined.AsReadOnly(), created, updated);
    }

    /// <summary>
    /// The cluster an item should be checked against before it may join, or null when
    /// similarity alone decides. Similarity to the nearest cluster not yet refused falls in
    /// one of three zones: at or above <paramref name="joinSimilarity"/> the item joins
    /// unchecked; from <paramref name="checkSimilarity"/> up to it, a judge decides; below
    /// it, the item starts its own cluster. An item refused <paramref name="maxChecks"/>
    /// times starts its own too.
    /// </summary>
    /// <remarks>
    /// On null, place the item with <see cref="Assign"/>, passing its refusals: it joins its
    /// nearest cluster if that is at or above the join threshold, and starts its own otherwise.
    /// When the judge says yes, add the item with <see cref="Join"/>; when it says no, add the
    /// cluster to <paramref name="refused"/> and ask again.
    /// </remarks>
    /// <param name="embedding">The item's embedding.</param>
    /// <param name="candidates">The clusters to consider, such as the item's nearest from a vector index.</param>
    /// <param name="refused">The ids of the clusters that already refused this item.</param>
    /// <param name="joinSimilarity">The similarity at or above which the item joins without a check.</param>
    /// <param name="checkSimilarity">The similarity at or above which a judge is asked; at most <paramref name="joinSimilarity"/>.</param>
    /// <param name="maxChecks">How many refusals an item takes before it starts its own cluster; zero or more.</param>
    /// <returns>The id of the cluster to put the question to, or null.</returns>
    public static long? ClusterToCheck(
        float[] embedding,
        IReadOnlyList<ClusterState> candidates,
        IReadOnlySet<long> refused,
        double joinSimilarity,
        double checkSimilarity,
        int maxChecks)
    {
        ArgumentNullException.ThrowIfNull(embedding);
        ArgumentNullException.ThrowIfNull(refused);
        Guard.Clusters(candidates, nameof(candidates), embedding.Length);
        Guard.Similarity(joinSimilarity, nameof(joinSimilarity));
        Guard.Similarity(checkSimilarity, nameof(checkSimilarity));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(checkSimilarity, joinSimilarity);
        ArgumentOutOfRangeException.ThrowIfNegative(maxChecks);
        if (refused.Count >= maxChecks)
        {
            return null;
        }

        var vector = Vectors.Normalize(embedding);
        ClusterState? best = null;
        var bestSimilarity = double.NegativeInfinity;
        foreach (var candidate in candidates)
        {
            if (refused.Contains(candidate.Id))
            {
                continue;
            }

            var similarity = Vectors.Dot(vector, Vectors.Normalize(candidate.Centroid));
            if (similarity > bestSimilarity)
            {
                bestSimilarity = similarity;
                best = candidate;
            }
        }

        return best is not null && bestSimilarity >= checkSimilarity && bestSimilarity < joinSimilarity ? best.Id : null;
    }

    /// <summary>
    /// Adds an item to a cluster, whatever their similarity: for an item a judge accepted.
    /// </summary>
    /// <param name="cluster">The cluster the item joins.</param>
    /// <param name="item">The item.</param>
    /// <returns>The cluster with the item folded into its centroid and one more member.</returns>
    /// <exception cref="ArgumentException">
    /// The vectors have different lengths, the count is negative, or the item's embedding is
    /// zero or holds a NaN or infinite component (such an item never joins a cluster).
    /// </exception>
    public static ClusterState Join(ClusterState cluster, EmbeddedItem item)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        Guard.Clusters([cluster], nameof(cluster));
        Guard.Dimensions(item.Embedding, cluster.Centroid.Length, $"Item {item.Id}'s embedding", nameof(item));
        var vector = Vectors.Normalize(item.Embedding);
        if (!vector.All(float.IsFinite) || vector.All(x => x == 0))
        {
            throw new ArgumentException($"Item {item.Id}'s embedding is zero or not finite, so it cannot join a cluster.", nameof(item));
        }

        var centroid = Vectors.Normalize(cluster.Centroid);
        Vectors.AddToMean(centroid, vector, cluster.Count + 1);
        return cluster with { Centroid = centroid, Count = cluster.Count + 1 };
    }

    /// <summary>
    /// The pairs of clusters close enough to be one, most similar first. Each cluster merges
    /// at most once per call, into the larger of the pair (on a tie, the one with the lower id).
    /// </summary>
    /// <param name="clusters">The clusters to compare, every pair of them: O(n²).</param>
    /// <param name="mergeSimilarity">The cosine similarity of two centroids at or above which they merge.</param>
    /// <returns>The merges, most similar first; ties are ordered by the ids involved.</returns>
    public static IReadOnlyList<MergeDecision> FindMerges(IReadOnlyList<ClusterState> clusters, double mergeSimilarity)
    {
        Guard.Clusters(clusters, nameof(clusters));
        Guard.Similarity(mergeSimilarity, nameof(mergeSimilarity));
        var normalized = clusters.Select(c => Vectors.Normalize(c.Centroid)).ToArray();
        var candidates = new List<MergeDecision>();
        for (var i = 0; i < clusters.Count; i++)
        {
            for (var j = i + 1; j < clusters.Count; j++)
            {
                AddIfClose(candidates, clusters[i], normalized[i], clusters[j], normalized[j], mergeSimilarity);
            }
        }

        return Pick(candidates);
    }

    /// <summary>
    /// <see cref="FindMerges(IReadOnlyList{ClusterState}, double)"/> over the given pairs only,
    /// by cluster id. A pair naming a cluster not in <paramref name="clusters"/>, a cluster
    /// with itself, or the same pair twice is ignored. The decisions are the same as the
    /// all-pairs check's whenever the pairs include every pair at or above
    /// <paramref name="mergeSimilarity"/>.
    /// </summary>
    /// <param name="clusters">The clusters the pairs name, with unique ids.</param>
    /// <param name="pairs">The candidate pairs, such as each changed cluster with its nearest neighbors.</param>
    /// <param name="mergeSimilarity">The cosine similarity of two centroids at or above which they merge.</param>
    /// <returns>The merges, most similar first; ties are ordered by the ids involved.</returns>
    public static IReadOnlyList<MergeDecision> FindMerges(
        IReadOnlyList<ClusterState> clusters, IEnumerable<(long A, long B)> pairs, double mergeSimilarity)
    {
        Guard.Clusters(clusters, nameof(clusters));
        ArgumentNullException.ThrowIfNull(pairs);
        Guard.Similarity(mergeSimilarity, nameof(mergeSimilarity));
        var byId = clusters.ToDictionary(c => c.Id, c => (Cluster: c, Centroid: Vectors.Normalize(c.Centroid)));

        var seen = new HashSet<(long, long)>();
        var candidates = new List<MergeDecision>();
        foreach (var (a, b) in pairs)
        {
            if (a == b || !seen.Add(a < b ? (a, b) : (b, a)))
            {
                continue;
            }

            if (byId.TryGetValue(a, out var x) && byId.TryGetValue(b, out var y))
            {
                AddIfClose(candidates, x.Cluster, x.Centroid, y.Cluster, y.Centroid, mergeSimilarity);
            }
        }

        return Pick(candidates);
    }

    private static void AddIfClose(
        List<MergeDecision> candidates, ClusterState a, float[] centroidA, ClusterState b, float[] centroidB, double mergeSimilarity)
    {
        var similarity = Vectors.Dot(centroidA, centroidB);

        // A NaN similarity (a broken centroid) is never close.
        if (!(similarity >= mergeSimilarity))
        {
            return;
        }

        var (into, from) = Larger(a, b);
        candidates.Add(new MergeDecision(from.Id, into.Id, similarity));
    }

    private static List<MergeDecision> Pick(List<MergeDecision> candidates)
    {
        var used = new HashSet<long>();
        var merges = new List<MergeDecision>();

        // Ordered by every field, so equal similarities (identical centroids) still give one answer.
        foreach (var merge in candidates.OrderByDescending(m => m.Similarity).ThenBy(m => m.FromId).ThenBy(m => m.IntoId))
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
    /// <param name="members">The members, with embeddings of one length.</param>
    /// <returns>The cohesion: 1 for no members; NaN when an embedding holds a NaN.</returns>
    public static double Cohesion(IReadOnlyList<EmbeddedItem> members)
    {
        Guard.Items(members, nameof(members));
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
    /// <paramref name="regroupSimilarity"/>. Members of parts smaller than
    /// <paramref name="minPartSize"/> stay with the largest part.
    /// </summary>
    /// <param name="members">The cluster's members, with unique ids.</param>
    /// <param name="cohesionThreshold">The cohesion under which the cluster is loose: above 0, at most 1.</param>
    /// <param name="regroupSimilarity">The join threshold for regrouping: above 0, at most 1.</param>
    /// <param name="minPartSize">The fewest members a part needs to stand alone: 1 or more.</param>
    /// <returns>
    /// The parts, largest first (the caller keeps the cluster's id on it), when at least two
    /// parts can stand alone; null when the cluster should stay whole, including when its
    /// cohesion is NaN.
    /// </returns>
    public static IReadOnlyList<NewCluster>? TrySplit(
        IReadOnlyList<EmbeddedItem> members, double cohesionThreshold, double regroupSimilarity, int minPartSize)
    {
        Guard.Items(members, nameof(members));
        Guard.Similarity(cohesionThreshold, nameof(cohesionThreshold));
        Guard.Similarity(regroupSimilarity, nameof(regroupSimilarity));
        ArgumentOutOfRangeException.ThrowIfLessThan(minPartSize, 1);
        if (members.Count < 2 * minPartSize || !(Cohesion(members) < cohesionThreshold))
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
