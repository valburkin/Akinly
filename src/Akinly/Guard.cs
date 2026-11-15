namespace Akinly;

/// <summary>Argument checks shared by the public entry points. Messages name ids and sizes, never content.</summary>
internal static class Guard
{
    /// <summary>A cosine-similarity threshold: finite, above 0 (so a zero vector can never reach it) and at most 1.</summary>
    public static void Similarity(double value, string paramName)
    {
        if (!double.IsFinite(value) || value <= 0 || value > 1)
        {
            throw new ArgumentOutOfRangeException(paramName, value, "A similarity threshold must be above 0 and at most 1.");
        }
    }

    /// <summary>
    /// Clusters with unique ids, non-negative counts and centroids of one length (or of
    /// <paramref name="dimensions"/>, when it is set). Returns the length, or -1 for no clusters.
    /// </summary>
    public static int Clusters(IReadOnlyList<ClusterState> clusters, string paramName, int dimensions = -1)
    {
        ArgumentNullException.ThrowIfNull(clusters, paramName);
        var ids = new HashSet<long>();
        foreach (var cluster in clusters)
        {
            if (cluster is null)
            {
                throw new ArgumentException("The list holds a null cluster.", paramName);
            }

            if (!ids.Add(cluster.Id))
            {
                throw new ArgumentException($"Cluster {cluster.Id} appears more than once.", paramName);
            }

            if (cluster.Count < 0)
            {
                throw new ArgumentException($"Cluster {cluster.Id} has a negative count.", paramName);
            }

            dimensions = Dimensions(cluster.Centroid, dimensions, $"Cluster {cluster.Id}'s centroid", paramName);
        }

        return dimensions;
    }

    /// <summary>
    /// Items with unique ids and embeddings of one length (or of <paramref name="dimensions"/>,
    /// when it is set). Returns the length, or the one given for no items.
    /// </summary>
    public static int Items(IReadOnlyList<EmbeddedItem> items, string paramName, int dimensions = -1)
    {
        ArgumentNullException.ThrowIfNull(items, paramName);
        var ids = new HashSet<long>();
        foreach (var item in items)
        {
            if (!ids.Add(item.Id))
            {
                throw new ArgumentException($"Item {item.Id} appears more than once.", paramName);
            }

            dimensions = Dimensions(item.Embedding, dimensions, $"Item {item.Id}'s embedding", paramName);
        }

        return dimensions;
    }

    /// <summary>One vector's length checked against the length expected so far (-1: none yet).</summary>
    public static int Dimensions(float[]? vector, int expected, string what, string paramName)
    {
        if (vector is null)
        {
            throw new ArgumentException($"{what} is missing.", paramName);
        }

        if (expected >= 0 && vector.Length != expected)
        {
            throw new ArgumentException($"{what} has {vector.Length} dimensions where {expected} were expected.", paramName);
        }

        return vector.Length;
    }

    /// <summary>Two vectors of the same length.</summary>
    public static void SameLength(float[] a, float[] b, string paramName)
    {
        if (a.Length != b.Length)
        {
            throw new ArgumentException($"The vectors have different lengths ({a.Length} and {b.Length}).", paramName);
        }
    }
}
