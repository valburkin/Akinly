namespace Akinly.VectorData.Records;

/// <summary>The vector of records that need none.</summary>
internal static class Placeholders
{
    public static ReadOnlyMemory<float> Value { get; } = new float[] { 1f };
}
