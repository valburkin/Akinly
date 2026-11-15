namespace Akinly.VectorData.Records;

#pragma warning disable CA1812 // Instantiated by the connectors through reflection.

/// <summary>A question, its state and answer, as JSON.</summary>
internal sealed class QuestionRecord<TKey>
{
    public TKey Key { get; set; } = default!;

    public long QuestionId { get; set; }

    public bool Open { get; set; }

    public string Body { get; set; } = "";

    /// <summary>A one-dimension placeholder, for connectors that require a vector on every record.</summary>
    public ReadOnlyMemory<float> Placeholder { get; set; } = Placeholders.Value;
}
