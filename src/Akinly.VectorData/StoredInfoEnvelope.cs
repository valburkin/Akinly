using Akinly.Storage;

namespace Akinly.VectorData;

/// <summary>The store's metadata record: its info, and the format of the records around it.</summary>
/// <param name="SchemaVersion">The record format, <see cref="VectorDataStore{TKey}.SchemaVersion"/> when written.</param>
/// <param name="Info">The store's info.</param>
internal sealed record StoredInfoEnvelope(int SchemaVersion, StoreInfo Info);
