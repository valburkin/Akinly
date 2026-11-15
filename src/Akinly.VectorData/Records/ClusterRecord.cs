namespace Akinly.VectorData.Records;

// The records the connectors map: plain classes with public settable properties, because every
// connector can map those; text fields hold what not every database can store natively.
#pragma warning disable CA1812 // Instantiated by the connectors through reflection.

/// <summary>A cluster, live or merged.</summary>
internal sealed class ClusterRecord<TKey>
{
    public TKey Key { get; set; } = default!;

    public long ClusterId { get; set; }

    /// <summary>False once merged into another; only live clusters are searched.</summary>
    public bool Live { get; set; }

    public bool Changed { get; set; }

    public int Count { get; set; }

    public string? Label { get; set; }

    public int LabeledAtCount { get; set; }

    public int RemovalsSinceRecompute { get; set; }

    /// <summary>The cluster it was merged into, or 0 while it is live.</summary>
    public long MergedIntoId { get; set; }

    /// <summary>The sum of the members' embeddings, in double precision (<see cref="SumCodec"/>).</summary>
    public string Sum { get; set; } = "";

    /// <summary>The clusters that refused one of its members, as a JSON array.</summary>
    public string RefusedBy { get; set; } = "[]";

    public ReadOnlyMemory<float> Centroid { get; set; }
}
