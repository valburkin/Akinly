namespace Akinly;

/// <summary>An item to place, with its embedding.</summary>
/// <param name="Id">The item's id, assigned by the caller.</param>
/// <param name="Embedding">The item's embedding, of any length; it is compared by direction only.</param>
public readonly record struct EmbeddedItem(long Id, float[] Embedding);
