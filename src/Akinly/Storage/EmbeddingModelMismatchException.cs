namespace Akinly.Storage;

/// <summary>
/// A clusterer configured for one embedding model was used on a store that holds embeddings
/// from another, or an embedding's length differs from the store's. Thresholds do not carry
/// over between models, so the clusterer refuses to mix them.
/// </summary>
public sealed class EmbeddingModelMismatchException : Exception
{
    /// <summary>A mismatch with no details.</summary>
    public EmbeddingModelMismatchException()
        : base("The embeddings do not match the store's embedding model.")
    {
    }

    /// <summary>A mismatch with a message.</summary>
    /// <param name="message">What happened.</param>
    public EmbeddingModelMismatchException(string message)
        : base(message)
    {
    }

    /// <summary>A mismatch caused by another exception.</summary>
    /// <param name="message">What happened.</param>
    /// <param name="innerException">The cause.</param>
    public EmbeddingModelMismatchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
