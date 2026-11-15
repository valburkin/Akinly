# Remove items

Remove an item when its source is gone: a ticket was deleted, a post was taken down, someone asked for their data to be erased. `RemoveAsync` takes the item out of its cluster and keeps the cluster's centroid exact.

## With a Clusterer

```csharp
using Akinly;
using Akinly.Storage;

var clusterer = new Clusterer(new InMemoryClusterStore(), ClusteringOptions.CosineOnly("toy-3d", joinSimilarity: 0.85));
await clusterer.AddAsync(
[
    new ClusterItem("ticket-1", new float[] { 1.00f, 0.00f, 0.00f }),
    new ClusterItem("ticket-3", new float[] { 0.97f, 0.26f, 0.00f }),
    new ClusterItem("ticket-4", new float[] { 0.00f, 0.00f, 1.00f }),
]);

// The author of ticket-4 asked for it to be deleted.
await clusterer.RemoveAsync(["ticket-4"]);

var cluster = await clusterer.GetClusterAsync(2);
Console.WriteLine($"cluster {cluster!.Id}: {cluster.Count} items");   // cluster 2: 0 items
Console.WriteLine(await clusterer.GetClusterOfAsync("ticket-4") is null);   // True

// A later ticket like it joins the same cluster again, by similarity.
var step = await clusterer.AddAsync([new ClusterItem("ticket-9", new float[] { 0.00f, 0.10f, 1.00f })]);
Console.WriteLine(step.Placements[0]);   // Placement { ItemId = ticket-9, ClusterId = 2, Reason = Similarity }
```

What a removal does:

- **A placed item** leaves its cluster. Its embedding is subtracted from the cluster's sum, so the centroid is again the mean of the members that remain. The cluster is marked changed, so the next `MaintainAsync` checks it for merges.
- **An item waiting for an answer** has its open question closed; an answer that arrives later is ignored.
- **An item waiting for room to ask** is dropped from the queue.
- **Unknown ids are ignored**, so removing twice is harmless.

A cluster left empty keeps its id and its last centroid. It stays in `ListClustersAsync` with a count of 0, and an item at or above the join threshold joins it again, as `ticket-9` did. A judge is never asked about an empty cluster, since there are no members to show.

To change an item's text or embedding, remove it and add it again: `AddAsync` refuses an id that is already in the store.

## With a TextClusterer

`TextClusterer.RemoveAsync` also forgets the texts. It removes them first, so even a removal that stops halfway never leaves a removed item's text behind:

```csharp
await textClusterer.RemoveAsync(["t1", "t2"]);
```

To keep an item in its cluster but stop keeping its text (a retention limit, say), use `ForgetTextAsync`:

```csharp
await textClusterer.ForgetTextAsync("t1");   // the item stays; questions that need its text are decided by similarity
```

## For deletion requests

`RemoveAsync` deletes the item's record, with its embedding, from the store. Some traces can remain, and you should know where:

| Where | What remains | What to do |
|---|---|---|
| The cluster's label | A label written while the item was a member can echo its text | Relabel the cluster (`ChatClientLabeler.LabelAsync` once it is due, or `WriteLabelAsync`) or clear it with `SetLabelAsync(id, null)` |
| Closed questions | Question records keep the item ids they named (`ItemId`, `ExampleItemIds`); they hold no text or embedding | If your item ids are personal data, use ids that are not, such as random ids mapped to your records in your own database |
| Your text store | Texts you keep yourself, outside `TextClusterer` | Remove them from your `IItemTextStore` (`TextClusterer.RemoveAsync` does this for its own) |
| Snapshots and backups | Earlier `ExportAsync` files and database backups | Expire or rewrite them by your retention policy |

A refusal the item caused stays recorded on its clusters (as cluster ids, not item data), so a merge it blocked stays blocked.

## Housekeeping

Removals subtract from sums in double precision. After 16 removals from one cluster, `MaintainAsync` recomputes its sum from its members, so rounding cannot build up. Run `MaintainAsync` after large removals, also because a cluster that lost members may now be close enough to merge with a neighbor.
