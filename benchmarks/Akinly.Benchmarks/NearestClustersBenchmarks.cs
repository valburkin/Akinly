using Akinly.Storage;
using BenchmarkDotNet.Attributes;

namespace Akinly.Benchmarks;

/// <summary>The in-memory store's exact nearest-cluster search, by store size.</summary>
[MemoryDiagnoser]
public class NearestClustersBenchmarks
{
    private InMemoryClusterStore _store = null!;
    private float[] _query = null!;

    [Params(1_000, 10_000, 50_000)]
    public int Clusters { get; set; }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _store = await Stores.ImportAsync(await Stores.SnapshotAsync(Clusters));
        _query = Stores.Vectors(1, seed: 99)[0];
    }

    [Benchmark(Description = "Nearest 10 clusters")]
    public Task<IReadOnlyList<ClusterNeighbor>> Nearest() => _store.NearestClustersAsync(_query, 10, default);
}
