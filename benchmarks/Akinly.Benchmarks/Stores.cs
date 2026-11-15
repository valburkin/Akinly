using Akinly.Storage;

namespace Akinly.Benchmarks;

/// <summary>
/// Stores of a given size, built once and copied for each iteration through a snapshot. The
/// data has structure, so every path runs: one cluster in ten has a near twin that maintenance
/// merges, and half of each new batch lands close to a cluster and joins it.
/// </summary>
internal static class Stores
{
    public const int Dimensions = 384;
    public const string Model = "benchmark";

    /// <summary>Join at 0.9, merge at 0.85: twins at 0.87 stay apart when added and merge in maintenance.</summary>
    public static ClusteringOptions Options() => ClusteringOptions.CosineOnly(Model, 0.9, 0.85);

    /// <summary>Random unit vectors: in 384 dimensions they are nearly orthogonal, so each starts its own cluster.</summary>
    public static float[][] Vectors(int count, int seed)
    {
        var random = new Random(seed);
        return [.. Enumerable.Range(0, count).Select(_ => RandomUnit(random))];
    }

    /// <summary>A store with <paramref name="clusters"/> clusters, a tenth of them twins of others, as a snapshot to import.</summary>
    public static async Task<byte[]> SnapshotAsync(int clusters)
    {
        var random = new Random(2);   // not the centers' seed, or each twin would be its center
        var centers = Vectors(clusters - (clusters / 10), seed: 1);
        var twins = centers.Take(clusters / 10).Select(c => Near(c, 0.87, random));
        var store = new InMemoryClusterStore();
        var clusterer = new Clusterer(store, Options());
        foreach (var batch in centers.Concat(twins).Select((v, i) => new ClusterItem($"seed{i}", v)).Chunk(1000))
        {
            await clusterer.AddAsync(batch);
        }

        using var stream = new MemoryStream();
        await store.ExportAsync(stream);
        return stream.ToArray();
    }

    /// <summary>A batch of new items: half close to existing clusters (they join), half new.</summary>
    public static ClusterItem[] Batch(int count, int clusters)
    {
        var random = new Random(7);
        var centers = Vectors(clusters - (clusters / 10), seed: 1);
        return [.. Enumerable.Range(0, count).Select(i => new ClusterItem(
            $"new{i}",
            i % 2 == 0 ? Near(centers[random.Next(centers.Length)], 0.95, random) : RandomUnit(random)))];
    }

    public static Task<InMemoryClusterStore> ImportAsync(byte[] snapshot) => InMemoryClusterStore.ImportAsync(new MemoryStream(snapshot));

    /// <summary>A unit vector at exactly <paramref name="similarity"/> to <paramref name="vector"/>, in a random direction.</summary>
    private static float[] Near(float[] vector, double similarity, Random random)
    {
        var other = RandomUnit(random);
        var along = Akinly.Vectors.Dot(other, vector);
        var orthogonal = Akinly.Vectors.Normalize([.. other.Select((x, d) => (float)(x - (along * vector[d])))]);
        var away = Math.Sqrt(1 - (similarity * similarity));
        return Akinly.Vectors.Normalize([.. vector.Select((x, d) => (float)((similarity * x) + (away * orthogonal[d])))]);
    }

    private static float[] RandomUnit(Random random) =>
        Akinly.Vectors.Normalize([.. Enumerable.Range(0, Dimensions).Select(_ => (float)((random.NextDouble() * 2) - 1))]);
}
