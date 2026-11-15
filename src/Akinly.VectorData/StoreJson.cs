using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Akinly.Storage;

namespace Akinly.VectorData;

/// <summary>Source-generated JSON for the fields stored as text: no reflection, safe to trim.</summary>
/// <remarks>Its code is generated, so it is left out of coverage; the store tests exercise it.</remarks>
[ExcludeFromCodeCoverage]
[JsonSourceGenerationOptions(UseStringEnumConverter = true, Converters = [typeof(FloatMemoryConverter), typeof(DoubleMemoryConverter)])]
[JsonSerializable(typeof(ChangeSet))]
[JsonSerializable(typeof(StoredInfoEnvelope))]
[JsonSerializable(typeof(JournalHeader))]
[JsonSerializable(typeof(StoredQuestion))]
[JsonSerializable(typeof(MergeCheck))]
[JsonSerializable(typeof(long[]))]
internal sealed partial class StoreJson : JsonSerializerContext;
