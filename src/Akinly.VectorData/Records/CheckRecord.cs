namespace Akinly.VectorData.Records;

#pragma warning disable CA1812 // Instantiated by the connectors through reflection.

/// <summary>A merge check, as JSON.</summary>
internal sealed class CheckRecord<TKey>
{
    public TKey Key { get; set; } = default!;

    public string Body { get; set; } = "";

    /// <summary>A one-dimension placeholder, for connectors that require a vector on every record.</summary>
    public ReadOnlyMemory<float> Placeholder { get; set; } = Placeholders.Value;
}
