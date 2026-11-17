# Run in ASP.NET Core or a worker service

Akinly registers with `Microsoft.Extensions.DependencyInjection`, binds its options from configuration, and validates them when the host starts. This guide builds a web API that accepts feedback, clusters it in the background, and lists the clusters.

## Register Akinly

`AddAkinly` registers a singleton `Clusterer`, its options, and an in-memory store. The builder it returns chooses the store, the judge, the labeler and the text clusterer:

```csharp
builder.Services.AddAkinly(builder.Configuration.GetSection(ClusteringOptions.SectionName))
    .UseChatClientJudge()       // Akinly.AI: a ChatClientJudge over the container's IChatClient
    .AddChatClientLabeler()     // Akinly.AI: a ChatClientLabeler over the same
    .AddTextClusterer();        // Akinly.AI: a TextClusterer over the container's IEmbeddingGenerator
```

The options come from the `Akinly` section of `appsettings.json`:

```json
{
  "Akinly": {
    "EmbeddingModel": "bge-m3",
    "JoinSimilarity": 0.96,
    "CheckSimilarity": 0.54,
    "MergeSimilarity": 0.88,
    "MergeCheckSimilarity": 0.75,
    "MaxOpenQuestions": 200,
    "FailedAnswers": { "MaxAttempts": 3, "RetryDelay": "00:00:02" }
  }
}
```

The four thresholds are the ones [calibrated](calibrate-thresholds.md#calibrated-values) for `bge-m3`, the embedding model registered below; the defaults suit OpenAI's `text-embedding-3-small`. Leave out any key to keep its default; [Configuration](../configuration.md#binding-from-configuration) lists them all. Without configuration, set the options in code: `AddAkinly(o => o.EmbeddingModel = "text-embedding-3-small")`.

The options are validated when the host starts: an invalid threshold stops the host with an `OptionsValidationException` listing every problem, rather than failing the first request. The judge's, labeler's and store's options are checked when you call `UseChatClientJudge`, `AddChatClientLabeler` and `UseVectorStore`, and throw an `ArgumentException` there.

## A complete web API

The app below uses a local Ollama ([setup](use-ollama.md)). It needs `Akinly.AI` and the OllamaSharp package.

`TextClusterer.AddAsync` waits for the model to answer every question it raises, which can take seconds or minutes, and calls on one instance run one at a time. So the endpoint does not cluster: it puts each item on a queue and returns at once, and a background service clusters the queue in batches.

```csharp
using System.Threading.Channels;
using Akinly;
using Akinly.AI;
using Microsoft.Extensions.AI;
using OllamaSharp;

var builder = WebApplication.CreateBuilder(args);

var ollama = new Uri(builder.Configuration["Ollama:Endpoint"] ?? "http://localhost:11434");
builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new OllamaApiClient(ollama, "bge-m3"));
builder.Services.AddSingleton<IChatClient>(new OllamaApiClient(ollama, "gemma4:12b"));

builder.Services.AddAkinly(builder.Configuration.GetSection(ClusteringOptions.SectionName))
    .UseChatClientJudge(o => o.MaxParallelRequests = 2)
    .AddChatClientLabeler()
    .AddTextClusterer();

builder.Services.AddSingleton(Channel.CreateUnbounded<TextItem>());
builder.Services.AddHostedService<ClusteringWorker>();

var app = builder.Build();

app.MapPost("/feedback", async (TextItem item, Channel<TextItem> queue) =>
{
    await queue.Writer.WriteAsync(item);
    return Results.Accepted();
});

app.MapGet("/clusters", (TextClusterer clusterer) =>
    clusterer.ListClustersAsync().Select(c => new { c.Id, c.Count, c.Label }));

app.MapGet("/clusters/{id:long}", async (long id, Clusterer clusterer) =>
    await clusterer.GetClusterAsync(id) is { } cluster
        ? Results.Ok(new { cluster.Id, cluster.Count, cluster.Label, Members = await clusterer.GetMembersAsync(cluster.Id) })
        : Results.NotFound());

app.Run();

/// <summary>Clusters the queued items, up to 100 per call.</summary>
sealed class ClusteringWorker(Channel<TextItem> queue, TextClusterer clusterer, ILogger<ClusteringWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (await queue.Reader.WaitToReadAsync(stoppingToken))
        {
            var batch = new List<TextItem>();
            while (batch.Count < 100 && queue.Reader.TryRead(out var item))
            {
                batch.Add(item);
            }

            try
            {
                var step = await clusterer.AddAsync(batch, stoppingToken);
                logger.LogInformation("Placed {Placed} items, merged {Merged} clusters", step.Placements.Count, step.Merges.Count);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Clustering a batch of {Count} items failed", batch.Count);
            }
        }
    }
}
```

Try it, with the address `dotnet run` prints:

```bash
curl -X POST http://localhost:5000/feedback -H "Content-Type: application/json" -d '{"id": "t1", "text": "The heating is broken"}'
curl http://localhost:5000/clusters
```

Notes on the design:

- **One `TextClusterer`, one writer.** `AddTextClusterer` registers a singleton, and the worker is the only caller that writes. That matters with a vector database store, which supports one writer at a time: run one instance of this service, or move the worker into a service of its own. The read endpoints can run anywhere.
- **Failures.** If the embedding model fails, nothing is added, and the batch is logged and dropped; queue it again if you need it. If `AddAsync` fails after the items were added (the judge's loop was canceled, say), the items stay with their questions open, and the next `AddAsync` settles them along with its own (or call `TextClusterer.SettleAsync`).
- **A durable queue.** `Channel` is in memory: items queued when the process stops are lost. For anything that matters, read from a durable queue or a table instead.
- **A merged id.** `GET /clusters/2` after cluster 2 merged into cluster 1 returns cluster 1: ids last, and old links keep working.

## A worker service

Without a web front end, the same registration works in a worker (`dotnet new worker`), with `Host.CreateApplicationBuilder`:

```csharp
var builder = Host.CreateApplicationBuilder(args);
// ... the same registrations as above ...
builder.Services.AddHostedService<ClusteringWorker>();
builder.Build().Run();
```

## With your own embeddings, no Akinly.AI

The core package alone registers a `Clusterer` you call with `ClusterItem`s:

```csharp
builder.Services.AddAkinly(o => o.EmbeddingModel = "text-embedding-3-small")
    .UseJudge<MyRuleJudge>();   // your IClusterJudge, if you answer questions live
```

Inject `Clusterer` (and `IClusterJudge`), call `AddAsync`, and answer questions with `clusterer.SettleAsync(judge)` or `AnswerAsync`. A `Clusterer` never calls a judge on its own: the registered judge is used by `TextClusterer`, and by your own `SettleAsync` calls.

## A different model for each role

Register chat clients under keys and name the key for each role, for example a small model for the many yes-or-no answers and a larger one for the few labels:

```csharp
builder.Services.AddKeyedSingleton<IChatClient>("judge", new OllamaApiClient(ollama, "gemma4:12b"));
builder.Services.AddKeyedSingleton<IChatClient>("labels", new OllamaApiClient(ollama, "a-larger-model"));

builder.Services.AddAkinly(builder.Configuration.GetSection(ClusteringOptions.SectionName))
    .UseChatClientJudge(chatClientKey: "judge")
    .AddChatClientLabeler(chatClientKey: "labels")
    .AddTextClusterer();
```

## Keep clusters and texts in a database

Swap the in-memory store for a vector database with `UseVectorStore` (from `Akinly.VectorData`), after registering the connector's `VectorStore`; see [Qdrant](use-qdrant.md#with-dependency-injection) and [PostgreSQL](use-postgresql.md#with-dependency-injection).

The judge, the labeler and the text clusterer share one `IItemTextStore` for the texts, an `InMemoryItemTextStore` unless you register your own:

```csharp
builder.Services.AddSingleton<IItemTextStore, MyDatabaseTextStore>();   // your implementation
```

Register it as `IItemTextStore`; registering only an `IItemTextSource` makes `UseChatClientJudge` throw, because the adapters read and write texts through one store.

## Configure the AI options

`UseChatClientJudge`, `AddChatClientLabeler` and `AddTextClusterer` take a callback for their options:

```csharp
builder.Services.AddAkinly(builder.Configuration.GetSection(ClusteringOptions.SectionName))
    .UseChatClientJudge(o =>
    {
        o.Criterion = "describing the same bug";
        o.RequestTimeout = TimeSpan.FromMinutes(5);
    })
    .AddChatClientLabeler(o => o.MinClusterSize = 5)
    .AddTextClusterer(o => o.EmbeddingBatchSize = 32);
```

With dependency injection, the judge's and labeler's options come from these callbacks; `TextClustererOptions.Judge` and `.Labeler` are not used.
