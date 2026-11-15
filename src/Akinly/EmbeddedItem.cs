namespace Akinly;

/// <summary>An item to place, with its embedding.</summary>
/// <param name="Id">The item's id, assigned by the caller.</param>
/// <param name="Embedding">
/// The item's embedding, compared by direction only. Every embedding and centroid in one call
/// must have the same length.
/// </param>
public readonly record struct EmbeddedItem(long Id, float[] Embedding);
