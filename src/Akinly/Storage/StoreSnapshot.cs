namespace Akinly.Storage;

/// <summary>The JSON form of a whole store, written by <see cref="InMemoryClusterStore.ExportAsync"/>.</summary>
/// <param name="SchemaVersion">The snapshot's format version; newer Akinly versions read every older one.</param>
/// <param name="Info">The store's metadata.</param>
/// <param name="Clusters">Every cluster, live and merged.</param>
/// <param name="Items">Every item.</param>
/// <param name="Questions">Every question, open and closed.</param>
/// <param name="MergeChecks">Every merge check.</param>
internal sealed record StoreSnapshot(
    int SchemaVersion,
    StoreInfo Info,
    List<StoredCluster> Clusters,
    List<StoredItem> Items,
    List<StoredQuestion> Questions,
    List<MergeCheck> MergeChecks)
{
    /// <summary>The format this version of Akinly writes.</summary>
    public const int CurrentSchemaVersion = 1;
}
