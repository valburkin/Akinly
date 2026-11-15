namespace Akinly;

/// <summary>An item to cluster: your id for it, and its embedding.</summary>
/// <param name="Id">Your id for the item, unique in the store.</param>
/// <param name="Embedding">
/// The item's embedding, from the model named in <see cref="ClusteringOptions.EmbeddingModel"/>.
/// Compared by direction only; every embedding in a store has the same length.
/// </param>
public sealed record ClusterItem(string Id, ReadOnlyMemory<float> Embedding);
