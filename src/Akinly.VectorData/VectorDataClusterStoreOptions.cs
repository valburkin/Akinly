using Microsoft.Extensions.VectorData;

namespace Akinly.VectorData;

/// <summary>How a <see cref="VectorDataClusterStore"/> lays out its collections.</summary>
public sealed class VectorDataClusterStoreOptions
{
    /// <summary>
    /// The length of the embeddings: required, because a vector collection is created with a
    /// fixed size. It must match the embedding model's output.
    /// </summary>
    public int Dimensions { get; set; }

    /// <summary>
    /// The start of the collections' names (<c>{prefix}_clusters</c>, <c>{prefix}_items</c> and so
    /// on): letters, digits, <c>_</c> and <c>-</c>, at most 48 characters, so every name fits
    /// PostgreSQL's limit of 63. Default <c>akinly</c>; use one prefix per independent store.
    /// </summary>
    public string CollectionPrefix { get; set; } = "akinly";

    /// <summary>
    /// What goes between the prefix and the collection's own name: <c>_</c> (the default),
    /// <c>-</c> for databases that allow no underscores in names (Azure AI Search), or nothing.
    /// </summary>
    public string NameSeparator { get; set; } = "_";

    /// <summary>The type of keys the connector accepts. Default UUIDs (GUIDs).</summary>
    public VectorDataKeyKind KeyKind { get; set; } = VectorDataKeyKind.Uuid;

    /// <summary>
    /// The distance function for the vector indexes, from <see cref="DistanceFunction"/>; one the
    /// connector supports. Default dot product: Akinly's vectors are unit length, so it ranks as
    /// cosine does, and the database keeps them as written (Qdrant normalizes vectors stored for
    /// cosine, which would change sums by rounding). Akinly recomputes similarities from the
    /// returned vectors, so only the order the index returns matters.
    /// </summary>
    public string DistanceFunction { get; set; } = Microsoft.Extensions.VectorData.DistanceFunction.DotProductSimilarity;

    /// <summary>
    /// The kind of vector index, from <see cref="IndexKind"/>, or null for the connector's default.
    /// An exact index (<see cref="IndexKind.Flat"/>) makes results reproducible; an approximate one
    /// (<see cref="IndexKind.Hnsw"/>) is faster on large stores.
    /// </summary>
    public string? IndexKind { get; set; }

    /// <summary>
    /// The most records one read fetches when it pages through a collection, one lookup asks for
    /// by key, or one write sends. Default 100. Pages of records with vectors are also kept under
    /// about 2 MB, so with long vectors they hold fewer (clusters of 3,072 dimensions: 42), which
    /// stays within Qdrant's 4 MB gRPC message limit.
    /// </summary>
    public int PageSize { get; set; } = 100;

    /// <summary>How many more neighbors than asked for a nearest-cluster search fetches, to make up for an approximate index. Default 8.</summary>
    public int SearchOversampling { get; set; } = 8;

    private const int MaxPrefixLength = 48;

    /// <summary>A copy of these options, so later changes to the original do not reach a store built from it.</summary>
    public VectorDataClusterStoreOptions Clone() => (VectorDataClusterStoreOptions)MemberwiseClone();

    /// <summary>The problems with these options; empty when they are valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Dimensions < 1)
        {
            errors.Add("Dimensions is required: the length of the embeddings.");
        }

        if (string.IsNullOrEmpty(CollectionPrefix) || CollectionPrefix.Length > MaxPrefixLength || !CollectionPrefix.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
        {
            errors.Add($"CollectionPrefix must be 1 to {MaxPrefixLength} letters, digits, underscores and dashes.");
        }

        if (NameSeparator is not ("_" or "-" or ""))
        {
            errors.Add("NameSeparator must be \"_\", \"-\" or empty.");
        }

        if (string.IsNullOrWhiteSpace(DistanceFunction))
        {
            errors.Add("DistanceFunction is required.");
        }

        if (PageSize < 1)
        {
            errors.Add("PageSize must be at least 1.");
        }

        if (SearchOversampling < 0)
        {
            errors.Add("SearchOversampling must not be negative.");
        }

        return errors;
    }

    /// <summary>The full name of one of the store's collections.</summary>
    internal string CollectionName(string name) => CollectionPrefix + NameSeparator + name;
}
