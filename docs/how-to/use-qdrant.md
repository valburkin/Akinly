# Keep clusters in Qdrant

`Akinly.VectorData` keeps the clusters, items, questions and metadata in a vector database through a Microsoft.Extensions.VectorData connector, and finds nearest clusters with the database's own index. This guide uses [Qdrant](https://qdrant.tech) through Semantic Kernel's Qdrant connector; the repository's contract tests run against it (image `qdrant/qdrant:v1.15.1`, connector 1.74.0-preview).

## Before you start

1. Run Qdrant. Locally, with Docker:

   ```bash
   docker run -p 6333:6333 -p 6334:6334 qdrant/qdrant:v1.15.1
   ```

   Port 6334 is the gRPC port the .NET client uses; 6333 serves the REST API and the web dashboard.

2. Reference `Akinly.VectorData` (and `Akinly.AI` if you embed with a model; see [Install](../getting-started.md#install)), and add the connector. It is a pre-release package:

   ```bash
   dotnet add package Microsoft.SemanticKernel.Connectors.Qdrant --prerelease
   ```

## Create the store

```csharp
using Akinly;
using Akinly.AI;
using Akinly.VectorData;
using Microsoft.SemanticKernel.Connectors.Qdrant;
using OllamaSharp;
using Qdrant.Client;

using var vectors = new QdrantVectorStore(new QdrantClient("localhost"), ownsClient: true);

var store = new VectorDataClusterStore(vectors, new VectorDataClusterStoreOptions
{
    Dimensions = 1024,             // the embedding model's output length: bge-m3 returns 1,024
    CollectionPrefix = "tickets",  // collections tickets_clusters, tickets_items, ...
});

var ollama = new Uri("http://localhost:11434");
using var embedder = new OllamaApiClient(ollama, "bge-m3");
using var chat = new OllamaApiClient(ollama, "gemma4:12b");

// The thresholds calibrated for bge-m3 (see Calibrate thresholds).
var options = new ClusteringOptions
{
    EmbeddingModel = "bge-m3",
    JoinSimilarity = 0.96,
    CheckSimilarity = 0.52,
    MergeSimilarity = 0.985,
    MergeCheckSimilarity = 0.66,
};

var clusterer = new TextClusterer(embedder, chat, options, store: store);

await clusterer.AddAsync([new TextItem("t1", "The heating in my apartment has been broken for two weeks.")]);
```

With your own embeddings, pass the store to a `Clusterer` instead: `new Clusterer(store, options)`.

The store creates its five collections on first use: `tickets_clusters` (centroid, sum, size, label), `tickets_items` (embedding, cluster, refusals), `tickets_questions`, `tickets_checks` (merge checks) and `tickets_meta` (version, embedding model, counters, and the write journal). Use one `CollectionPrefix` per independent set of clusters, such as one per tenant or per embedding model.

For Qdrant Cloud or a secured server, give the client its address, TLS and key: `new QdrantClient("your-cluster.cloud.qdrant.io", https: true, apiKey: key)`.

## Settings for Qdrant

| Option | Use | Why |
|---|---|---|
| `KeyKind` | `VectorDataKeyKind.Uuid` (the default) | Qdrant accepts only UUIDs and unsigned integers as keys. The store derives a name-based UUID from each Akinly id and keeps the original ids in data fields |
| `DistanceFunction` | `DotProductSimilarity` (the default) | Akinly's vectors are unit length, so the dot product ranks as cosine does. Qdrant normalizes vectors stored for cosine distance, and that rounding would make cluster sums drift as members leave |
| `IndexKind` | null (the default: Qdrant's HNSW index) | Qdrant's index is approximate. Akinly recomputes every similarity from the returned vectors and fetches `SearchOversampling` (8) extra neighbors to make up for misses, but an approximate index can still return slightly different candidates from run to run |
| `PageSize` | 100 (the default) | Pages of records with vectors are also kept under about 2 MB, within Qdrant's default gRPC message limit of 4 MB |

Qdrant compares numbers in range filters as doubles, so ids and sequence numbers are exact up to 2^53, far more than any store hands out.

## Things to know

- **One writer at a time.** Qdrant has no transactions across collections and no compare-and-swap, so run the clusterer in one process: a single instance of your service, or a lock of your own around every write. Other processes can read. Each write is journaled first, so if a process stops halfway, the next write finishes it. See [One writer](../concepts.md#one-writer).
- **Reads can see a write in progress.** A reader in another process can see part of a change set, such as an item placed in a cluster whose count has not moved yet.
- **Large calls with long vectors.** A nearest-cluster search returns each cluster with its centroid and its sum, and it is not paged: it asks for `CandidateClusters` (10) plus `SearchOversampling` (8) clusters, plus one more for every cluster the current call has already changed. A large `AddAsync` with long vectors can make that answer pass Qdrant's 4 MB limit; see [Troubleshooting](../troubleshooting.md#qdrant-message-too-large). Adding a few hundred items per call keeps it small.
- **Texts are not stored here.** The store keeps ids, vectors and labels. `TextClusterer` keeps texts in its `IItemTextStore`, in memory by default, so after a restart the judge has no texts for the items added before it, and questions it cannot put fairly become `Unanswerable`. To keep texts across restarts, implement `IItemTextStore` over your database and pass it as `texts:`. Its three methods are `GetTextAsync`, `SetTextAsync` and `RemoveTextAsync`, each by item id.
- **Treat embeddings as sensitive.** Much of a text can be recovered from its embedding; secure the database as you would the texts.

## With dependency injection

Register the connector's `VectorStore`, then tell Akinly to use it:

```csharp
using Akinly;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.Connectors.Qdrant;
using Qdrant.Client;

builder.Services.AddSingleton<VectorStore>(_ => new QdrantVectorStore(new QdrantClient("localhost"), ownsClient: true));
builder.Services.AddAkinly(builder.Configuration.GetSection(ClusteringOptions.SectionName))
    .UseVectorStore(o =>
    {
        o.Dimensions = 1024;
        o.CollectionPrefix = "tickets";
    });
```

If the container holds several vector stores, register Qdrant's under a key and pass it: `.UseVectorStore(o => o.Dimensions = 1024, vectorStoreKey: "clusters")`. The connector also has its own registration methods (`AddQdrantVectorStore`). See [Run in ASP.NET Core or a worker service](dependency-injection.md) for a complete host.
