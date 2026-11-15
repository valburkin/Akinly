# Save and restore the in-memory store

`InMemoryClusterStore` keeps everything in the process's memory. To keep clusters between runs without a database, export the store as JSON and import it in the next run. The clusters, their ids, the open questions and every refusal come back exactly as they were.

## Save and load

```csharp
using Akinly;
using Akinly.Storage;

var options = ClusteringOptions.CosineOnly("toy-3d", joinSimilarity: 0.85);
var store = new InMemoryClusterStore();
var clusterer = new Clusterer(store, options);
await clusterer.AddAsync([new ClusterItem("ticket-1", new float[] { 1.00f, 0.00f, 0.00f })]);

await using (var file = File.Create("clusters.json"))
{
    await store.ExportAsync(file);
}

// In a later run:
InMemoryClusterStore restored;
await using (var file = File.OpenRead("clusters.json"))
{
    restored = await InMemoryClusterStore.ImportAsync(file);
}

var again = new Clusterer(restored, options);
var step = await again.AddAsync([new ClusterItem("ticket-3", new float[] { 0.97f, 0.26f, 0.00f })]);
// ticket-3 joins cluster 1, which came back from the file
```

With dependency injection, import before you register, and hand the store to Akinly: `AddAkinly(...).UseStore(restored)`. With `TextClusterer`, pass it as `store:`.

## When to save

Any time. `ExportAsync` takes its snapshot under the store's lock, and every write is applied whole under the same lock, so a snapshot never holds half a change, even while other calls run. Save after each batch, on a timer, or when the process shuts down (in a host, from `IHostApplicationLifetime.ApplicationStopping` or a hosted service's `StopAsync`).

A crash while writing the file can leave it half-written. Write to a temporary file and move it into place, so the previous snapshot survives until the new one is complete:

```csharp
await using (var file = File.Create("clusters.json.tmp"))
{
    await store.ExportAsync(file);
}

File.Move("clusters.json.tmp", "clusters.json", overwrite: true);
```

## What the file holds

One JSON document with a schema version and the whole store:

- the metadata: the store's version, the embedding model and its dimensions, and the counters for the next cluster id, question id and item sequence;
- every cluster, live and merged (merged ones keep their redirect), with its centroid, the sum of its members, its size, label and refusals;
- every item, with its embedding, its cluster or its open question, and the clusters that refused it;
- every question, open and closed;
- every merge check (pairs a judge answered, or a split separated).

Embeddings and sums are written as JSON numbers of about ten characters each, so the file grows with the number of items times the embedding length: roughly 10 KB per item at 1,024 dimensions, plus each cluster's centroid and sum.

It does not hold texts. `TextClusterer` and the judge read texts from an `IItemTextStore`, in memory by default; keep them yourself (in a file or a database behind your own `IItemTextStore`) if a judge or labeler should see older items after a restart. Without them, a question whose texts are gone is `Unanswerable`, and similarity decides.

## Import checks

`ImportAsync` validates the snapshot before it returns a store, and throws an `InvalidDataException` when the JSON is not a snapshot, is empty, comes from a newer schema version than this Akinly reads, or contradicts itself (a cluster whose count does not match its members, a redirect loop, an item in a state its cluster or question contradicts, counts of open questions or waiting items that do not match). A newer Akinly reads every older schema version.

Store snapshots like any data holding embeddings: much of a text can be recovered from its embedding.

## Outgrowing memory

The in-memory store searches by scanning every cluster, and the whole store must fit in memory and in one file. When that gets slow or large, or when several services must read the clusters, keep them in a vector database: [Qdrant](use-qdrant.md) or [PostgreSQL with pgvector](use-postgresql.md). Akinly has no built-in copy from a snapshot to a database store.
