# Keep clusters in PostgreSQL with pgvector

`Akinly.VectorData` keeps the clusters in PostgreSQL through Semantic Kernel's pgvector connector, with the [pgvector](https://github.com/pgvector/pgvector) extension for vector search. The repository's contract tests run against it (image `pgvector/pgvector:0.8.0-pg17`, connector 1.74.0-preview).

## Before you start

1. Run PostgreSQL with pgvector. Locally, with Docker:

   ```bash
   docker run -e POSTGRES_PASSWORD=postgres -p 5432:5432 pgvector/pgvector:0.8.0-pg17
   ```

   On your own server, install the pgvector extension. If the user Akinly connects as may not create extensions, run `CREATE EXTENSION IF NOT EXISTS vector;` once in the database as an administrator.

2. Reference `Akinly.VectorData` (see [Install](../getting-started.md#install)) and add the connector, a pre-release package:

   ```bash
   dotnet add package Microsoft.SemanticKernel.Connectors.PgVector --prerelease
   ```

## Create the store

```csharp
using Akinly;
using Akinly.VectorData;
using Microsoft.SemanticKernel.Connectors.PgVector;

var connectionString = "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=postgres";
using var vectors = new PostgresVectorStore(connectionString);

var store = new VectorDataClusterStore(vectors, new VectorDataClusterStoreOptions
{
    Dimensions = 1024,             // the embedding model's output length
    CollectionPrefix = "tickets",  // tables tickets_clusters, tickets_items, ...
});

// The thresholds calibrated for bge-m3 (see Calibrate thresholds).
var options = new ClusteringOptions
{
    EmbeddingModel = "bge-m3",
    JoinSimilarity = 0.96,
    CheckSimilarity = 0.54,
    MergeSimilarity = 0.88,
    MergeCheckSimilarity = 0.75,
};

var clusterer = new Clusterer(store, options);
```

With `Akinly.AI`, pass the store to `TextClusterer` instead: `new TextClusterer(embedder, chat, options, store: store)`.

The store creates five tables on first use: `tickets_clusters`, `tickets_items`, `tickets_questions`, `tickets_checks` (merge checks) and `tickets_meta` (version, embedding model, counters, and the write journal). The prefix is limited to 48 characters so every name fits PostgreSQL's limit of 63. Keep connection strings with passwords out of source code.

## Exact or approximate search

The pgvector connector creates **no vector index by default** (`IndexKind.Flat`): every nearest-cluster search scans the clusters table and is exact, so results are reproducible. That is the right choice until the scan gets slow.

For large stores, ask for an HNSW index:

```csharp
using Microsoft.Extensions.VectorData;

var options = new VectorDataClusterStoreOptions
{
    Dimensions = 1024,
    IndexKind = IndexKind.Hnsw,
};
```

Choose it before the first write: the tables and their indexes are created once, on first use.

## HNSW and filtered search

pgvector applies a query's filter after the HNSW index scan. Akinly's searches are filtered: nearest clusters skip merged ones, and a cluster's examples are searched among its own items. When the matching rows are a small share of the table, an HNSW scan can return fewer rows than asked for. For examples, the store notices a short answer and ranks the cluster's members itself; for nearest clusters, a short answer can hide a candidate, and an item may start a new cluster where it should have joined one.

Fixes, in order of preference:

- With pgvector 0.8 or later, turn on iterative index scans, which keep scanning until enough rows pass the filter. Akinly re-sorts the results itself, so the relaxed order is fine:

  ```sql
  ALTER DATABASE postgres SET hnsw.iterative_scan = relaxed_order;
  ```

  (A database-level setting applies to new connections.)
- Raise `hnsw.ef_search` (default 40), the number of candidates one scan considers, the same way.
- Stay with the default exact search (`IndexKind.Flat`).

## Things to know

- **One writer at a time.** PostgreSQL is transactional, but the connector does not expose transactions across tables, so the store works as with any vector database: one writing process, a journal so a stopped write is finished by the next one, and readers that can see a write in progress. See [One writer](../concepts.md#one-writer).
- **Texts are not stored here.** The tables hold ids, vectors and labels. For a judge or labeler after a restart, keep texts in your own `IItemTextStore` (a table of id and text works); see [Keep clusters in Qdrant](use-qdrant.md#things-to-know).
- **Embeddings are sensitive.** Much of a text can be recovered from its embedding; protect the tables like the texts.

## With dependency injection

```csharp
using Akinly;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.Connectors.PgVector;

var connectionString = builder.Configuration.GetConnectionString("Clusters")
    ?? throw new InvalidOperationException("Set ConnectionStrings:Clusters.");

builder.Services.AddSingleton<VectorStore>(_ => new PostgresVectorStore(connectionString));
builder.Services.AddAkinly(builder.Configuration.GetSection(ClusteringOptions.SectionName))
    .UseVectorStore(o => o.Dimensions = 1024);
```

The connector also has its own registration methods (`AddPostgresVectorStore`). See [Run in ASP.NET Core or a worker service](dependency-injection.md) for a complete host.
