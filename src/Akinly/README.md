# Akinly

Incremental clustering of short texts for .NET, by embedding similarity. Items (support tickets, feedback, survey answers, posts) join a cluster or start one as they arrive; clusters keep their ids for good, and when two merge you are told which id lives on. Clear cases are decided by similarity; close calls come back as questions for a judge (a language model, a rule, or a person) that you answer whenever you like.

This is the core package: the `Clusterer`, its options, questions and verdicts, the `IClusterStore` contract with an in-memory store, dependency injection, and telemetry. It needs no model and no database, and depends only on Microsoft.Extensions abstractions and System.Numerics.Tensors.

- For texts in and clusters out with any model through Microsoft.Extensions.AI (Ollama, OpenAI, Azure OpenAI and others), add [Akinly.AI](https://www.nuget.org/packages/Akinly.AI).
- To keep clusters in Qdrant, PostgreSQL with pgvector or another vector database, add [Akinly.VectorData](https://www.nuget.org/packages/Akinly.VectorData).

## Install

```bash
dotnet add package Akinly
```

Requires .NET 10.

## Example

Embeddings come from any model; three-dimensional vectors keep this example short.

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

var maintained = await clusterer.MaintainAsync();   // cluster 2 merges into cluster 1
```

With options that have an ask zone (the defaults: join at 0.83, ask a judge from 0.30), close calls come back in `step.Questions`. Answer them with `AnswerAsync` when you have the answers, or loop with `SettleAsync(items, judge)`.

## Documentation

- [Getting started](https://github.com/valburkin/Akinly/blob/main/docs/getting-started.md)
- [Concepts](https://github.com/valburkin/Akinly/blob/main/docs/concepts.md): zones, questions, merges, lasting ids
- [Configuration](https://github.com/valburkin/Akinly/blob/main/docs/configuration.md)
- [Answer questions yourself or through a batch API](https://github.com/valburkin/Akinly/blob/main/docs/how-to/answer-questions-yourself.md)
- [All guides](https://github.com/valburkin/Akinly/blob/main/docs/README.md), [troubleshooting](https://github.com/valburkin/Akinly/blob/main/docs/troubleshooting.md) and the [changelog](https://github.com/valburkin/Akinly/blob/main/CHANGELOG.md)

Akinly is open source under the [MIT license](https://github.com/valburkin/Akinly/blob/main/LICENSE), on [GitHub](https://github.com/valburkin/Akinly). It was written with Claude Code, Anthropic's AI coding assistant, under the maintainer's direction; [how it was built](https://github.com/valburkin/Akinly#how-this-was-built).
