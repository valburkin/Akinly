namespace Akinly.VectorData;

/// <summary>The journal's header record, written after its parts: a change set is journaled once this exists.</summary>
/// <param name="Version">The version the change set moves the store to.</param>
/// <param name="Parts">How many part records hold the change set's JSON.</param>
/// <param name="SchemaVersion">The format it was written in; a newer one is refused.</param>
internal sealed record JournalHeader(long Version, int Parts, int SchemaVersion = 1);
