namespace Akinly.VectorData.Records;

#pragma warning disable CA1812 // Instantiated by the connectors through reflection.

/// <summary>The store's metadata, or its journal: a change set being applied.</summary>
internal sealed class MetaRecord<TKey>
{
    public TKey Key { get; set; } = default!;

    /// <summary>What it is: <c>info</c>, <c>journal</c> (the header) or <c>journal0</c>, <c>journal1</c>… (the parts).</summary>
    public string Name { get; set; } = "";

    public string Body { get; set; } = "";

    /// <summary>A one-dimension placeholder, for connectors that require a vector on every record.</summary>
    public ReadOnlyMemory<float> Placeholder { get; set; } = Placeholders.Value;
}
