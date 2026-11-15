# Akinly.VectorData

An [Akinly](https://www.nuget.org/packages/Akinly) cluster store over any vector database with a Microsoft.Extensions.VectorData connector. Clusters, items, questions, merge checks and metadata live in five collections, and nearest clusters come from the database's own vector index. Qdrant and PostgreSQL with pgvector are tested.

- **One writer at a time.** Vector databases offer neither transactions across collections nor compare-and-swap, so run the clusterer in one process; any number can read.
- **Crash-safe writes.** Each change set is checked for what a database could not store, journaled, then written; a write stopped halfway is finished by the next one.
- **Any key type.** Akinly's ids map to UUID keys (Qdrant, PostgreSQL) or text keys, with the original ids kept in data fields.

## Install

```bash
dotnet add package Akinly.VectorData
dotnet add package Microsoft.SemanticKernel.Connectors.Qdrant --prerelease   # or the connector for your database
```

Requires .NET 10, and a connector built against Microsoft.Extensions.VectorData.Abstractions 10.x.

## Example

With Qdrant running locally (`docker run -p 6333:6333 -p 6334:6334 qdrant/qdrant:v1.15.1`):

```csharp
using Akinly;
using Akinly.VectorData;
using Microsoft.SemanticKernel.Connectors.Qdrant;
using Qdrant.Client;

using var vectors = new QdrantVectorStore(new QdrantClient("localhost"), ownsClient: true);

var store = new VectorDataClusterStore(vectors, new VectorDataClusterStoreOptions
{
    Dimensions = 1024,             // your embedding model's output length
    CollectionPrefix = "tickets",  // collections tickets_clusters, tickets_items, ...
});

// The thresholds calibrated for bge-m3 (see Calibrate thresholds).
var options = new ClusteringOptions
{
    EmbeddingModel = "bge-m3",
    JoinSimilarity = 0.96,
    CheckSimilarity = 0.52,
    MergeSimilarity = 0.985,
    MergeCheckSimilarity = 0.66,
};

var clusterer = new Clusterer(store, options);
```

With dependency injection, register the connector's `VectorStore` and call `UseVectorStore`:

```csharp
using Microsoft.Extensions.VectorData;

builder.Services.AddSingleton<VectorStore>(_ => new QdrantVectorStore(new QdrantClient("localhost"), ownsClient: true));
builder.Services.AddAkinly(o => o.EmbeddingModel = "bge-m3")
    .UseVectorStore(o => o.Dimensions = 1024);
```

## Documentation

- [Keep clusters in Qdrant](https://github.com/valburkin/Akinly/blob/main/docs/how-to/use-qdrant.md)
- [Keep clusters in PostgreSQL with pgvector](https://github.com/valburkin/Akinly/blob/main/docs/how-to/use-postgresql.md), including HNSW and filtered search
- [Configuration of the store](https://github.com/valburkin/Akinly/blob/main/docs/configuration.md#vectordataclusterstoreoptions)
- [One writer, and why](https://github.com/valburkin/Akinly/blob/main/docs/concepts.md#one-writer)
- [Troubleshooting](https://github.com/valburkin/Akinly/blob/main/docs/troubleshooting.md), [all guides](https://github.com/valburkin/Akinly/blob/main/docs/README.md) and the [changelog](https://github.com/valburkin/Akinly/blob/main/CHANGELOG.md)

Akinly is open source under the [MIT license](https://github.com/valburkin/Akinly/blob/main/LICENSE), on [GitHub](https://github.com/valburkin/Akinly). It was written with Claude Code, Anthropic's AI coding assistant, under the maintainer's direction; [how it was built](https://github.com/valburkin/Akinly#how-this-was-built).
