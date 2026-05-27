namespace Akinly;

/// <summary>
/// The default <see cref="IClustererFactory"/>. The clusterers keep no state between calls,
/// so one instance of each is shared, and the factory is safe to use from several threads.
/// </summary>
public sealed class ClustererFactory : IClustererFactory
{
    private readonly LeaderCosineClusterer _leader = new();
    private readonly GreedyCosineClusterer _greedy = new();

    /// <inheritdoc />
    public IClusterer Resolve(ClusterAlgorithm algorithm) => algorithm switch
    {
        ClusterAlgorithm.Leader => _leader,
        ClusterAlgorithm.Greedy => _greedy,
        _ => throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm, "Not a known clustering algorithm."),
    };
}
