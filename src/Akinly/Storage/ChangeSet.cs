namespace Akinly.Storage;

/// <summary>
/// Everything one clusterer call changed, written by <see cref="IClusterStore.ApplyAsync"/> as one
/// unit. Every entry is a whole record to upsert by id, so applying a change set twice leaves
/// the store as applying it once.
/// </summary>
/// <param name="Info">The store's metadata after the change; its version is one more than before.</param>
/// <param name="Clusters">Clusters to upsert.</param>
/// <param name="Items">Items to upsert.</param>
/// <param name="RemovedItemIds">Items to delete.</param>
/// <param name="Questions">Questions to upsert.</param>
/// <param name="MergeChecks">Merge checks to upsert.</param>
public sealed record ChangeSet(
    StoreInfo Info,
    IReadOnlyList<StoredCluster> Clusters,
    IReadOnlyList<StoredItem> Items,
    IReadOnlyList<string> RemovedItemIds,
    IReadOnlyList<StoredQuestion> Questions,
    IReadOnlyList<MergeCheck> MergeChecks)
{
    /// <summary>Whether the change set holds no record changes (only, possibly, new metadata).</summary>
    public bool IsEmpty =>
        Clusters.Count == 0 && Items.Count == 0 && RemovedItemIds.Count == 0 && Questions.Count == 0 && MergeChecks.Count == 0;
}
