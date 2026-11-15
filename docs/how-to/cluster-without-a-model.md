# Cluster without a language model

Cosine-only clustering needs no judge: an item joins its nearest cluster at or above one threshold and starts its own below it, and clusters merge at or above another. It is free, fast and deterministic, and a fair choice when your texts are distinct enough, or when you want a baseline before you add a judge. What it cannot do is tell "the same topic" from "the same thing" in the band where they look alike; that is what the judge is for.

## Options

`ClusteringOptions.CosineOnly` builds options with no ask zone: `CheckSimilarity` equals `JoinSimilarity`, and `MergeCheckSimilarity` equals `MergeSimilarity`, so no question is ever asked.

```csharp
var options = ClusteringOptions.CosineOnly("text-embedding-3-small", joinSimilarity: 0.85, mergeSimilarity: 0.88);
```

Leave out `mergeSimilarity` to merge at the join threshold. A merge threshold a little above the join threshold keeps maintenance from folding together clusters that items alone kept apart; one below it merges more eagerly. Both are thresholds of your embedding model: [calibrate them](calibrate-thresholds.md), whose output suggests a `JoinSimilarity` and a `MergeSimilarity`.

## With your own embeddings

The core package is enough. This example runs as is (three-dimensional vectors stand in for real embeddings):

```csharp
using Akinly;
using Akinly.Storage;

var clusterer = new Clusterer(new InMemoryClusterStore(), ClusteringOptions.CosineOnly("toy-3d", joinSimilarity: 0.85));

var step = await clusterer.AddAsync(
[
    new ClusterItem("ticket-1", new float[] { 1.00f, 0.00f, 0.00f }),
    new ClusterItem("ticket-2", new float[] { 0.82f, 0.57f, 0.00f }),
    new ClusterItem("ticket-3", new float[] { 0.97f, 0.26f, 0.00f }),
    new ClusterItem("ticket-4", new float[] { 0.00f, 0.00f, 1.00f }),
]);
// ticket-1 -> cluster 1, ticket-2 -> cluster 2, ticket-3 -> cluster 1, ticket-4 -> cluster 3

var maintained = await clusterer.MaintainAsync();
// cluster 2 merged into cluster 1
```

Call `MaintainAsync` after each batch: in a cosine-only setup it is the only thing that corrects arrival order, by merging clusters that drifted together. [Getting started](../getting-started.md#your-first-clusterer) walks through each placement.

## With an embedding model

`TextClusterer` embeds the texts for you. Pass `chat: null`, and options without an ask zone (it refuses options that would need a judge):

```csharp
using Akinly;
using Akinly.AI;
using OllamaSharp;

using var embedder = new OllamaApiClient(new Uri("http://localhost:11434"), "bge-m3");

var clusterer = new TextClusterer(
    embedder,
    chat: null,
    ClusteringOptions.CosineOnly("bge-m3", joinSimilarity: 0.96));   // the join threshold calibrated for bge-m3

await clusterer.AddAsync(
[
    new TextItem("t1", "The heating in my apartment has been broken for two weeks."),
    new TextItem("t2", "There is no heat in our unit and it is freezing."),
]);
```

Any `IEmbeddingGenerator<string, Embedding<float>>` works in place of Ollama's: see [Use OpenAI or any other provider](use-openai.md).

If you pass a chat client with cosine-only options, `TextClusterer` asks no questions but uses the model to write labels; see [Label clusters](label-clusters.md).

## Splitting loose clusters

Cosine-only clusters can grow loose: each new member is close to the centroid, but the centroid drifts, and the cluster ends up holding two things. Splits regroup such clusters during `MaintainAsync`. They are off by default, and allowed only without an ask zone (when a judge approved each member, a similarity number should not undo that).

```csharp
var options = ClusteringOptions.CosineOnly("text-embedding-3-small", joinSimilarity: 0.85, mergeSimilarity: 0.88);
options.Split.Enabled = true;             // regroup loose clusters during MaintainAsync
options.Split.CohesionThreshold = 0.80;   // loose: members average below 0.80 similarity to the centroid
options.Split.RegroupSimilarity = 0.85;   // the join threshold used to regroup its members
options.Split.MinPartSize = 3;            // a part needs at least 3 members to stand alone
```

A cluster is considered only once it has twice `MinPartSize` members. When it splits, the largest part keeps the id, members of parts too small to stand alone stay with it, the other parts become new clusters, and the step reports a `ClusterSplit(ClusterId, NewClusterIds)`. The parts are recorded as different from each other, so the next merge check does not put them back together.

The values above are the defaults, not measured for any model; calibrate them like the other thresholds, for example by checking the cohesion (`IncrementalClusterer.Cohesion`) of clusters you know are good and of ones you know are mixed.

## When to add a judge

Signs that cosine similarity alone is not enough for your data:

- Raising the threshold until clusters are clean leaves most items alone in clusters of one.
- Lowering it until related items group produces a few broad clusters of "anything about billing".
- Calibration warns that no similarity reached 95% precision for joins.

Then keep your thresholds as the join and merge thresholds, add a check threshold below each, and give the clusterer a judge: [a model](use-ollama.md), [a rule, a person or a batch job](answer-questions-yourself.md).
