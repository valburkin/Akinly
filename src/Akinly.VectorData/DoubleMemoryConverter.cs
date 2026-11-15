using System.Text.Json;
using System.Text.Json.Serialization;

namespace Akinly.VectorData;

/// <summary>Sums in the journal as base64, the same way (<see cref="Records.SumCodec"/>).</summary>
internal sealed class DoubleMemoryConverter : JsonConverter<ReadOnlyMemory<double>>
{
    public override ReadOnlyMemory<double> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Records.SumCodec.Decode(reader.GetString() ?? "");

    public override void Write(Utf8JsonWriter writer, ReadOnlyMemory<double> value, JsonSerializerOptions options) =>
        writer.WriteStringValue(Records.SumCodec.Encode(value.Span));
}
