namespace Akinly;

/// <summary>
/// Resolves the clusterer for an algorithm at call time, so that each dataset (or each
/// tenant's settings) can use its own instead of one chosen at startup.
/// </summary>
public interface IClustererFactory
{
    /// <summary>The clusterer that implements <paramref name="algorithm"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="algorithm"/> is not a defined value.</exception>
    IClusterer Resolve(ClusterAlgorithm algorithm);
}
