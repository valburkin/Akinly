using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Akinly.Storage;

/// <summary>Source-generated JSON for snapshots: no reflection, safe to trim.</summary>
/// <remarks>Its code is generated, so it is left out of coverage; the round-trip tests exercise it.</remarks>
[ExcludeFromCodeCoverage]
[JsonSourceGenerationOptions(WriteIndented = false, UseStringEnumConverter = true)]
[JsonSerializable(typeof(StoreSnapshot))]
internal sealed partial class StoreJsonContext : JsonSerializerContext;
