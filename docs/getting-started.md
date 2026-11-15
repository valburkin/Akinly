# Getting started

This guide installs Akinly, clusters a few items, reads the results, and keeps them between runs. It takes about ten minutes. For the ideas behind each step, see [Concepts](concepts.md).

- [Install](#install)
- [Choose a setup](#choose-a-setup)
- [Your first clusterer](#your-first-clusterer)
- [Read the results](#read-the-results)
- [Keep clusters tidy](#keep-clusters-tidy)
- [Add a language model](#add-a-language-model)
- [Keep the clusters between runs](#keep-the-clusters-between-runs)
- [Next steps](#next-steps)

## Install

Akinly needs .NET 10. Add the packages you need from nuget.org:

```bash
dotnet add package Akinly              # the core
dotnet add package Akinly.AI           # optional: models
dotnet add package Akinly.VectorData   # optional: vector databases
```

`Akinly.AI` and `Akinly.VectorData` depend on `Akinly`, so you never need to add it twice.

## Choose a setup

Akinly has three entry points, each built on the one below. Pick the highest one that fits.

| You have | You want | Use |
|---|---|---|
| Texts, an embedding model and a chat model | Clusters, with the close calls decided by the model as you add | `TextClusterer` (`Akinly.AI`) |
| Texts and an embedding model, no chat model | Clusters by similarity alone | `TextClusterer` with `ClusteringOptions.CosineOnly` and no chat client |
| Embeddings from anywhere | Clusters, with the close calls answered by you, later, or in a batch | `Clusterer` (`Akinly`) |
| Embeddings, and clusters kept in your own data structures | Placement decisions only | `IncrementalClusterer` (`Akinly`), [the pure functions](concepts.md#three-layers) |

The examples below start with `Clusterer`, because it runs without any model and shows what happens at each step. `TextClusterer` does the same steps for you; see [Add a language model](#add-a-language-model).

## Your first clusterer

A `Clusterer` places items in clusters and keeps everything in a store. Here the store is in memory, and the options are cosine only: an item joins its nearest cluster when their similarity is at least 0.85, and starts a cluster of its own otherwise.

Every item has your id for it and an embedding. Real embeddings come from a model and have hundreds of dimensions; these have three, so the example runs as is and the numbers are easy to follow.

```csharp
using Akinly;
using Akinly.Storage;

var clusterer = new Clusterer(new InMemoryClusterStore(), ClusteringOptions.CosineOnly("toy-3d", joinSimilarity: 0.85));

var step = await clusterer.AddAsync(
[
    new ClusterItem("ticket-1", new float[] { 1.00f, 0.00f, 0.00f }),   // "The heating is broken"
    new ClusterItem("ticket-2", new float[] { 0.82f, 0.57f, 0.00f }),   // "Heating and hot water are both out"
    new ClusterItem("ticket-3", new float[] { 0.97f, 0.26f, 0.00f }),   // "No heat in my apartment"
    new ClusterItem("ticket-4", new float[] { 0.00f, 0.00f, 1.00f }),   // "My rent went up 30 percent"
]);

foreach (var placement in step.Placements)
{
    Console.WriteLine($"{placement.ItemId} -> cluster {placement.ClusterId} ({placement.Reason})");
}
// ticket-1 -> cluster 1 (NewCluster)
// ticket-2 -> cluster 2 (NewCluster)
// ticket-3 -> cluster 1 (Similarity)
// ticket-4 -> cluster 3 (NewCluster)
```

What happened, item by item:

1. `ticket-1` found no cluster, so it started cluster 1.
2. `ticket-2` is 0.82 similar to cluster 1: below 0.85, so it started cluster 2.
3. `ticket-3` is 0.97 similar to cluster 1 and 0.94 to cluster 2. It joined the nearest, cluster 1.
4. `ticket-4` is unlike anything so far and started cluster 3.

The first argument of `CosineOnly`, `"toy-3d"`, names the embedding model. The store records it, and a clusterer configured for another model refuses to work on that store, because similarity thresholds do not carry over between models.

`AddAsync` returns a `ClusteringStep`: the placements it made, the merges and splits, and the questions it needs answered (none here, since cosine-only options never ask).

## Read the results

```csharp
await foreach (var cluster in clusterer.ListClustersAsync())
{
    var members = await clusterer.GetMembersAsync(cluster.Id);
    Console.WriteLine($"cluster {cluster.Id}: {cluster.Count} items: {string.Join(", ", members)}");
}

Console.WriteLine(await clusterer.GetClusterOfAsync("ticket-2"));   // the cluster an item is in
Console.WriteLine((await clusterer.GetClusterAsync(2))?.Id);        // a merged id leads to the cluster it merged into
```

| Method | Returns |
|---|---|
| `ListClustersAsync()` | Every live cluster (not merged into another), by id, as `Cluster` records: `Id`, `Count`, `Label`, `Centroid`, `LabeledAtCount` |
| `GetClusterAsync(id)` | One cluster; a merged cluster's id leads to the cluster it merged into. Null when there is no such cluster |
| `GetMembersAsync(id)` | The ids of a cluster's members, in the order they were added; follows merges too |
| `GetClusterOfAsync(itemId)` | The cluster an item is in, or null while it waits for an answer or when it is not in the store |

Run after the maintenance step below, this prints:

```text
cluster 1: 3 items: ticket-1, ticket-2, ticket-3
cluster 3: 1 items: ticket-4
1
1
```

Cluster ids are handed out in order (1, 2, 3, ...) and never reused. Store them, put them in URLs, and show them on pages: they last.

## Keep clusters tidy

Items are placed in arrival order, so an early item can start a cluster that later turns out to duplicate another. `ticket-3` pulled cluster 1 toward cluster 2; they are now 0.89 similar. `MaintainAsync` compares the clusters that changed since its last call with their nearest neighbors and merges the pairs that are close enough (0.85 here, the merge threshold `CosineOnly` copies from the join threshold):

```csharp
var maintained = await clusterer.MaintainAsync();
foreach (var merge in maintained.Merges)
{
    Console.WriteLine($"cluster {merge.FromId} merged into cluster {merge.IntoId}");
}
// cluster 2 merged into cluster 1
```

The larger cluster keeps its id. The smaller one's id now redirects to it: `GetClusterAsync(2)` returns cluster 1, and so does `GetMembersAsync(2)`. If you stored cluster 2 anywhere, update the reference or follow the redirect when you read it.

Call `MaintainAsync` every so often, for example after each batch you add. It checks at most `MaxClustersPerMaintain` (1,000) changed clusters per call and merges each cluster at most once per call, so a busy store may need a few calls to settle. `TextClusterer` calls it after every add for you.

## Add a language model

With cosine similarity alone you must choose one threshold, and no threshold separates "the same problem" from "the same topic" well. Akinly's main feature is a middle band of similarity, the ask zone, in which a judge decides. With `Akinly.AI`, the judge is a chat model, and `TextClusterer` embeds the texts too:

```csharp
using Akinly;
using Akinly.AI;
using Microsoft.Extensions.AI;
using OllamaSharp;
using OllamaSharp.Models.Chat;

var ollama = new Uri("http://localhost:11434");
using var embedder = new OllamaApiClient(ollama, "qwen3-embedding:8b");   // embeds the texts
using var chat = new OllamaApiClient(ollama, "gemma4:12b");               // decides close calls and writes labels

// Thresholds calibrated for qwen3-embedding:8b; the defaults are for OpenAI's text-embedding-3-small.
var options = new ClusteringOptions
{
    EmbeddingModel = "qwen3-embedding:8b",
    JoinSimilarity = 0.96,
    CheckSimilarity = 0.55,
    MergeSimilarity = 0.97,
    MergeCheckSimilarity = 0.71,
};

// A short answer needs no reasoning: with thinking off, the model answers much faster.
var noThinking = new ChatOptions { RawRepresentationFactory = _ => new ChatRequest { Think = false } };
var textOptions = new TextClustererOptions { Judge = { ChatOptions = noThinking }, Labeler = { ChatOptions = noThinking } };

var clusterer = new TextClusterer(embedder, chat, options, textOptions);

var step = await clusterer.AddAsync(
[
    new TextItem("t1", "The heating in my apartment has been broken for two weeks."),
    new TextItem("t2", "There is no heat in our unit and it is freezing."),
]);
```

`AddAsync` embeds the texts, places them, puts every close call to the model and applies its answers, checks for merges, and labels clusters that reached three members. It returns everything that happened as one `ClusteringStep`. `TextClusterer` has the same read methods as `Clusterer` (`ListClustersAsync`, `GetMembersAsync`), and its `Clusterer` property gives the rest.

The default thresholds (join at 0.83, ask from 0.30) were calibrated for OpenAI's `text-embedding-3-small`; the values above are calibrated for `qwen3-embedding:8b`, and [Calibrate thresholds](how-to/calibrate-thresholds.md#calibrated-values) lists others. For any other embedding model, calibrate them. Model setup: [Ollama](how-to/use-ollama.md), [OpenAI and others](how-to/use-openai.md).

## Keep the clusters between runs

The in-memory store lives as long as your process. To keep it, save it as JSON and load it again:

```csharp
var options = ClusteringOptions.CosineOnly("toy-3d", joinSimilarity: 0.85);
var store = new InMemoryClusterStore();
var clusterer = new Clusterer(store, options);
// ... add items ...

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
```

[Save and restore the in-memory store](how-to/save-and-restore.md) covers what the file holds and what it does not (texts). For data that outgrows memory, or that several services read, keep the clusters in a vector database: [Qdrant](how-to/use-qdrant.md) or [PostgreSQL with pgvector](how-to/use-postgresql.md).

## Next steps

- [Concepts](concepts.md): zones, questions, refusals, merges and the store, in depth.
- [Answer questions yourself or through a batch API](how-to/answer-questions-yourself.md): the ask zone without a live model.
- [Calibrate thresholds](how-to/calibrate-thresholds.md) for your embedding model.
- [Configuration](configuration.md): every option.
- [Run in ASP.NET Core or a worker service](how-to/dependency-injection.md).
