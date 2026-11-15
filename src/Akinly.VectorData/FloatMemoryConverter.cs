using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Akinly.VectorData;

/// <summary>
/// Vectors in the journal as base64 of their little-endian values: exact, and about a third of
/// the size of JSON numbers.
/// </summary>
internal sealed class FloatMemoryConverter : JsonConverter<ReadOnlyMemory<float>>
{
    public override ReadOnlyMemory<float> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var bytes = reader.GetBytesFromBase64();
        var values = new float[bytes.Length / sizeof(float)];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * sizeof(float)));
        }

        return values;
    }

    public override void Write(Utf8JsonWriter writer, ReadOnlyMemory<float> value, JsonSerializerOptions options)
    {
        var bytes = new byte[value.Length * sizeof(float)];
        for (var i = 0; i < value.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float)), value.Span[i]);
        }

        writer.WriteBase64StringValue(bytes);
    }
}
