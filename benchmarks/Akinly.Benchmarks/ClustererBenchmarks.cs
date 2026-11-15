using BenchmarkDotNet.Attributes;

namespace Akinly.Benchmarks;

/// <summary>
/// Adding a batch of new items (half join a cluster, half start one), and a maintenance pass over
/// changed clusters (a tenth of them have a twin to merge with), by store size. Each iteration
/// starts from a fresh copy of the store, so one call is measured at a time.
/// </summary>
[MemoryDiagnoser]
public class ClustererBenchmarks
{
    private byte[] _snapshot = null!;
    private ClusterItem[][] _batches = null!;
    private Clusterer _clusterer = null!;

    private const int BatchesPerCall = 10;

    [Params(1_000, 10_000)]
    public int Clusters { get; set; }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _snapshot = await Stores.SnapshotAsync(Clusters);
        _batches = [.. Stores.Batch(BatchesPerCall * 100, Clusters).Chunk(100)];

        // The data must exercise what the descriptions say: joins when adding, merges when maintaining.
        var check = new Clusterer(await Stores.ImportAsync(_snapshot), Stores.Options());
        var added = await check.AddAsync(_batches[0]);
        var maintained = await new Clusterer(await Stores.ImportAsync(_snapshot), Stores.Options()).MaintainAsync();
        if (added.Placements.Count(p => p.Reason == PlacementReason.Similarity) < 40 || maintained.Merges.Count == 0)
        {
            throw new InvalidOperationException($"The benchmark data no longer makes items join and clusters merge: {string.Join(",", added.Placements.GroupBy(p => p.Reason).Select(g => $"{g.Key}={g.Count()}"))}, {maintained.Merges.Count} merges.");
        }
    }

    [IterationSetup]
    public void Copy() => _clusterer = new Clusterer(Stores.ImportAsync(_snapshot).GetAwaiter().GetResult(), Stores.Options());

    /// <summary>Ten batches a call, so one measurement is long enough to time well; results are per batch.</summary>
    [Benchmark(Description = "Add 100 items", OperationsPerInvoke = BatchesPerCall)]
    public async Task Add()
    {
        foreach (var batch in _batches)
        {
            await _clusterer.AddAsync(batch);
        }
    }

    [Benchmark(Description = "Maintain (up to 1,000 changed clusters)")]
    public Task<ClusteringStep> Maintain() => _clusterer.MaintainAsync();
}
