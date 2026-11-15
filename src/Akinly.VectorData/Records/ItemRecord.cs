namespace Akinly.VectorData.Records;

#pragma warning disable CA1812 // Instantiated by the connectors through reflection.

/// <summary>An item.</summary>
internal sealed class ItemRecord<TKey>
{
    public TKey Key { get; set; } = default!;

    /// <summary>The caller's id. The key is derived from it, so it is read, never searched.</summary>
    public string ItemId { get; set; } = "";

    public long Sequence { get; set; }

    /// <summary>Its cluster, or 0 while it waits.</summary>
    public long ClusterId { get; set; }

    /// <summary>The <see cref="Storage.ItemState"/>, as a number.</summary>
    public int State { get; set; }

    /// <summary>The clusters that refused it, as a JSON array.</summary>
    public string RefusedClusterIds { get; set; } = "[]";

    /// <summary>Its open question, or 0.</summary>
    public long OpenQuestionId { get; set; }

    public bool JudgeUnavailable { get; set; }

    public ReadOnlyMemory<float> Embedding { get; set; }
}
