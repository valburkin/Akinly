# Configuration

Every option Akinly reads, with its default, what it does, and when to change it. For the behavior behind each one, see [Concepts](concepts.md).

- [ClusteringOptions](#clusteringoptions)
  - [Thresholds](#thresholds)
  - [Limits](#limits)
  - [Merges](#merges)
  - [Failed answers](#failed-answers)
  - [Splits](#splits)
  - [Presets and helpers](#presets-and-helpers)
- [ChatClientJudgeOptions](#chatclientjudgeoptions) (`Akinly.AI`)
- [ChatClientLabelerOptions](#chatclientlabeleroptions) (`Akinly.AI`)
- [TextClustererOptions](#textclustereroptions) (`Akinly.AI`)
- [VectorDataClusterStoreOptions](#vectordataclusterstoreoptions) (`Akinly.VectorData`)
- [Validation](#validation)
- [Binding from configuration](#binding-from-configuration)
- [Registration methods](#registration-methods)

Every options class is copied when a clusterer, judge, labeler or store is built from it, so changing the instance afterwards has no effect. Build a new one to change settings.

## ClusteringOptions

`ClusteringOptions` (namespace `Akinly`) tells the `Clusterer` how to decide. Its configuration section is `Akinly` (`ClusteringOptions.SectionName`).

### Thresholds

All four are cosine similarities, above 0 and at most 1. They belong to an embedding model: the defaults were calibrated for OpenAI's `text-embedding-3-small` at its default dimensions, with the repository's `calibrate` command, on 6,000 texts pooled from the training splits of Banking77, CLINC150 and MASSIVE (287 groups). With any other model, [calibrate them](how-to/calibrate-thresholds.md).

The ask zones are wide on purpose: the judge, not similarity, decides the close calls, and `MaxChecksPerItem` bounds the questions per item. The thresholds never shift at run time. To prefer precision or recall, use the matching set from the calibration report together with the judge's `Preference`.

| Option | Default | What it does | When to change it |
|---|---|---|---|
| `EmbeddingModel` | `""` (required) | Names the model the embeddings come from. The store records it on the first add, and a clusterer configured with another name refuses to work on that store (`EmbeddingModelMismatchException`), since thresholds do not carry over between models | Always set it. Use the same string everywhere for one model; the comparison is exact |
| `JoinSimilarity` | 0.83 | At or above it, an item joins its nearest cluster without a question | Raise it if unrelated items end up together without being asked about; lower it if near-duplicates go to the judge or start new clusters |
| `CheckSimilarity` | 0.30 | At or above it, and below `JoinSimilarity`, a judge decides whether the item joins. Must not exceed `JoinSimilarity`; equal to it means no ask zone | Lower it to let the judge see more candidates (more questions); raise it to ask less. Set it to `JoinSimilarity` for cosine-only clustering |
| `MergeSimilarity` | 0.72 | At or above this similarity of two clusters (see `MergeLinkage`), they merge without a question | Raise it if clusters about different things merge; lower it if duplicates stay apart |
| `MergeCheckSimilarity` | 0.55 | At or above it, and below `MergeSimilarity`, a judge decides whether two clusters merge. Must not exceed `MergeSimilarity` | As for `CheckSimilarity`, for merges |

### Limits

| Option | Default | What it does | When to change it |
|---|---|---|---|
| `MaxChecksPerItem` | 3 | Refusals an item takes before it stops asking; after that, it joins a cluster only at the join threshold, or starts its own. A "none" to a question with choices counts one refusal for each cluster offered. 0 means join questions are never asked | Raise it when items often belong to a cluster further down their nearest; lower it to ask fewer questions per item |
| `MaxOpenQuestions` | 200 | Questions open at once. While it is reached, new items that need a question wait, and merge questions wait for a later `MaintainAsync` | Lower is more accurate (an item placed while its neighbors' answers are out cannot see them); higher lets more questions go out at once, such as in one large batch |
| `CandidateClusters` | 10 | Nearest clusters fetched when placing an item (the nearest decides; the next ones are offered as choices, and stand in when it is empty or refused the item) and for each cluster in a merge check | Raise it with high `MaxChecksPerItem`, or if merges are missed because the partner is not among the ten nearest. Each search returns more records |
| `MaxClustersPerMaintain` | 1,000 | Changed clusters one `MaintainAsync` checks. The next call continues after the last one checked, wrapping around | Lower it to bound the time of one call on a large store; raise it to settle a busy store in fewer calls |
| `JoinChoices` | 3 | Clusters offered in one join question, nearest first, among those with members. The nearest must be at or above `CheckSimilarity`; the others are offered with it whatever their similarity, so the judge sees how fine the clusters around the item are. 1 asks yes or no about the nearest | 1 for a judge of your own that answers only yes or no; more shows the judge more context, in longer prompts |
| `ExamplesPerQuestion` | 10 | Members nearest the centroid shown to the judge with a join question, for each cluster offered | Lower it to save tokens; 0 shows only the label (a question about a cluster with neither examples nor a label cannot be put) |
| `MergeExamplesPerCluster` | 8 | Members of each cluster shown to the judge with a merge question | As above, for merges |

### Merges

| Option | Default | What it does | When to change it |
|---|---|---|---|
| `MergeRefusals` | `Ask` | When one cluster refused an item that is now in the other: `Ask` leaves the pair to a judge, never merging it on similarity alone; `Block` never merges it; `Ignore` merges it like any other pair. With no merge ask zone, `Ask` works as `Block` | `Block` if a merge must never carry an item into a cluster that refused it, at the cost of duplicate clusters. `MergeRespectsRefusals`, its 1.0 form, still works: `true` is `Block`, `false` is `Ignore` |
| `MergeLinkage` | `Average` | How two clusters' similarity is measured for `MergeSimilarity` and `MergeCheckSimilarity`: `Average`, the average similarity of a member of one to a member of the other; `Centroid`, the similarity of their centroids, as in 1.0 | `Centroid` only to keep merge thresholds you calibrated with 1.0. On mixed clusters a centroid is misleadingly close to other centroids, so merges snowball |

### Failed answers

`FailedAnswers` (`FailedAnswerOptions`) sets what happens when a judge does not answer. A failure is never a refusal.

| Option | Default | What it does | When to change it |
|---|---|---|---|
| `FailedAnswers.MaxAttempts` | 3 | Failed answers a question takes before the clusterer gives up on it. Then a join is decided by similarity alone, and a merge is not made (the pair is asked again once either cluster changes) | Raise it for a flaky provider you would rather wait for; 1 gives up at the first failure |
| `FailedAnswers.RetryDelay` | 1 second | How long `SettleAsync` waits after a round with a failed answer; after rounds in which every answer failed, doubled each time, up to 5 minutes. At most 1 hour. Not used when you call `AnswerAsync` yourself | Raise it for a provider that rate-limits; `TimeSpan.Zero` retries at once (tests) |

### Splits

`Split` (`SplitOptions`) regroups loose clusters during `MaintainAsync`. Off by default, and allowed only when the options have no ask zone (see [Splits](concepts.md#splits)).

| Option | Default | What it does |
|---|---|---|
| `Split.Enabled` | `false` | Whether loose clusters are split |
| `Split.CohesionThreshold` | 0.80 | The mean similarity of members to their centroid under which a cluster is loose |
| `Split.RegroupSimilarity` | 0.85 | The join threshold used to regroup a loose cluster's members |
| `Split.MinPartSize` | 3 | The fewest members a part needs to stand alone; smaller parts stay with the largest |

Turn splits on in a cosine-only setup whose merge threshold is low enough to chain unrelated clusters together. Calibrate `CohesionThreshold` and `RegroupSimilarity` with your embedding model, like the other thresholds.

### Presets and helpers

| Member | What it does |
|---|---|
| `ClusteringOptions.CosineOnly(embeddingModel, joinSimilarity, mergeSimilarity = null)` | Options with no ask zone: `CheckSimilarity` equals `JoinSimilarity`, and `MergeSimilarity` and `MergeCheckSimilarity` equal `mergeSimilarity` (the join threshold when omitted). No judge is needed |
| `HasAskZone` | Whether the options leave a zone in which a judge decides joins or merges |
| `Validate()` | The problems with the options, one sentence each; empty when they are valid |
| `ThrowIfInvalid()` | Throws an `ArgumentException` listing every problem |
| `Clone()` | A deep copy |

## ChatClientJudgeOptions

How a `ChatClientJudge` asks its model. Pass them to `new ChatClientJudge(chat, texts, options)`, set `TextClustererOptions.Judge`, or configure them in `UseChatClientJudge(o => ...)`.

| Option | Default | What it does | When to change it |
|---|---|---|---|
| `Criterion` | "about the same specific problem or request, so that one answer or fix would settle both; sharing a topic is not enough" (`DefaultCriterion`) | What "the same" means for your data. It completes the prompt's sentence "Two texts belong together only when they are ..." | Fit it to your data; it is the strongest control over how fine the clusters are. The default suits complaints, feedback and tickets, where one fix settles both. For requests to an assistant or a service, where two texts ask for the same kind of thing with different details: "asking for the same kind of request: the same action or the same information, whatever the details such as names, dates, places or amounts; sharing a topic is not enough". For news: "about the same news story or event; sharing a broad topic is not enough". Others: "describing the same bug", "requesting the same feature" |
| `JoinInstructions` | null (uses `JudgePrompts.DefaultJoinInstructions`) | The system prompt for yes-or-no join questions; `{criterion}` is replaced with `Criterion` | Only if changing `Criterion` is not enough. Keep the JSON answer format, or set `UseStructuredOutput` and check that answers still parse |
| `ChoiceInstructions` | null (uses `JudgePrompts.DefaultChoiceInstructions`) | The system prompt for join questions with choices; the answer is `{"group": n}`, 0 for none. The default tells the model that the groups were picked only because they look similar, and to pick one only if the new text asks for the same specific thing as its members | As above |
| `MergeMembersEachWay` | 1 | How a merge question is put to the model. Above 0, as yes-or-no join questions: whether each of this many members of one cluster (those nearest its centroid) belongs in the other, both ways; the clusters merge only if every answer is yes, and the first no ends it. 0 asks one question that compares the two groups as a whole (`MergeInstructions`), as 1.0 did | Leave it: a model places one text in a group far more reliably than it compares two groups, which on texts written to one template it tends to find alike. Raise it to 2 for more care before merging, at more requests per merge question. If you send requests yourself, build them with `JudgePrompts.BuildRequestsAsync` |
| `MergeInstructions` | null (uses `JudgePrompts.DefaultMergeInstructions`) | The system prompt for merge questions asked as a whole (`MergeMembersEachWay = 0`). The default calls two clusters one when a typical member of one would fit just as well in the other; a few odd members do not matter | As above |
| `Preference` | `Balanced` | Which mistake the model should rather make on a close call (`ClusteringPreference`). `Precision` adds "When you are unsure, answer that they do not belong together." (with choices: pick none); `Recall` adds "When you are unsure, answer that they belong together." (with choices: pick the best group rather than none); `Balanced` adds nothing. The sentence goes after the instructions, the defaults or your own | When you would rather leave some duplicates apart (`Precision`) or join some unlike items (`Recall`). Pair it with the matching threshold set from the [calibration report](how-to/calibrate-thresholds.md#preferences) |
| `IncludeLabels` | `true` | Whether clusters' labels go into the prompts | Turn it off if labels are not reviewed, so text from one item that ended up in a label cannot reach every later question about its cluster |
| `MaxParallelRequests` | 4 | Questions put to the model at once | Lower it for a local model on one GPU (1 or 2); raise it for a cloud provider within its rate limits |
| `RequestTimeout` | 2 minutes | How long one request may take before it counts as a failed answer; null for no limit of the judge's own | Raise it for a slow local model; lower it to fail fast |
| `MaxTextLength` | 1,000 | Texts longer than this many characters are cut (with "…"), bounding a prompt's size | Raise it for longer items, at the cost of tokens |
| `UseStructuredOutput` | `true` | Asks for the answer through a JSON schema (`{"same": boolean}`, or `{"group": integer}` for a question with choices) | Turn it off for a provider or model that rejects response schemas; answers are read either way |
| `UseZeroTemperature` | `false` | Sends a temperature of 0 when `ChatOptions` sets none, for steadier answers. Off by default because current Claude models and OpenAI's reasoning models reject any temperature | Turn it on for a model that accepts one (most local models do) |
| `ChatOptions` | null | Options sent with every request, such as `ModelId`. Copied per request; tools and conversation state are always removed | To pick a model per request, set provider-specific options, or cap output tokens |

## ChatClientLabelerOptions

How a `ChatClientLabeler` titles clusters. Pass them to `new ChatClientLabeler(chat, texts, options)`, set `TextClustererOptions.Labeler`, or configure them in `AddChatClientLabeler(o => ...)`.

| Option | Default | What it does |
|---|---|---|
| `Instructions` | `DefaultInstructions` | The system prompt; `{maxLength}` is replaced with `MaxLength` |
| `MinClusterSize` | 3 | The fewest members a cluster needs to get a label |
| `RelabelGrowth` | 2 | A cluster is labeled again once its size has grown or shrunk by this factor since its last label. Must be above 1 |
| `ExamplesPerLabel` | 10 | Members nearest the centroid the model reads |
| `MaxLength` | 120 | The longest label accepted, in characters; at least 10 |
| `MaxTextLength` | 1,000 | Texts longer than this many characters are cut |
| `RequestTimeout` | 2 minutes | How long one request may take; null for no limit of the labeler's own |
| `UseStructuredOutput` | `true` | Asks for `{"label": "..."}` through a JSON schema |
| `UseZeroTemperature` | `false` | Sends a temperature of 0 when `ChatOptions` sets none. Off by default because current Claude models and OpenAI's reasoning models reject any temperature |
| `ChatOptions` | null | Options sent with every request; tools and conversation state are always removed |

A label is accepted only if it is one line of at most `MaxLength` characters with no braces, double quotes, backticks or control characters (quotes around the whole label are trimmed); otherwise the cluster keeps its old label.

## TextClustererOptions

How a `TextClusterer` runs the steps around the clusterer.

| Option | Default | What it does |
|---|---|---|
| `EmbeddingBatchSize` | 64 | Texts sent to the embedding model in one request; at least 1 |
| `MaintainAfterAdd` | `true` | Whether each `AddAsync` ends with `MaintainAsync` (and settles the merge questions it raises) |
| `LabelClusters` | `true` | Whether clusters touched by an add are labeled when due. Needs a labeler: a chat client in the constructor, or `AddChatClientLabeler` with dependency injection |
| `Judge` | defaults | The judge's options, used when the `TextClusterer` builds its own judge (the constructor that takes a chat client). Ignored with dependency injection: configure `UseChatClientJudge` instead |
| `Labeler` | defaults | The labeler's options, used when the `TextClusterer` builds its own labeler. Ignored with dependency injection: configure `AddChatClientLabeler` instead |

When the `ClusteringOptions` given to `TextClusterer` leave `EmbeddingModel` empty, the embedding generator's default model id (from its `EmbeddingGeneratorMetadata`) is used. Setting it yourself is clearer.

## VectorDataClusterStoreOptions

How a `VectorDataClusterStore` lays out its collections. Pass them to `new VectorDataClusterStore(vectorStore, options)` or configure them in `UseVectorStore(o => ...)`.

| Option | Default | What it does | When to change it |
|---|---|---|---|
| `Dimensions` | 0 (required) | The length of the embeddings; vector collections are created with a fixed size | Always set it to your embedding model's output length |
| `CollectionPrefix` | `akinly` | The start of the collections' names: 1 to 48 letters, digits, `_` and `-` | Use one prefix per independent store (per tenant, per embedding model) |
| `NameSeparator` | `_` | What goes between the prefix and each collection's name (`clusters`, `items`, `questions`, `checks`, `meta`): `_`, `-` or empty | `-` for databases that allow no underscores in names |
| `KeyKind` | `Uuid` | The key type the connector accepts: `Uuid` (name-based UUIDs derived from Akinly's ids) or `Text` (strings such as `cluster-42`) | `Text` for connectors that accept only string keys. Qdrant needs `Uuid` |
| `DistanceFunction` | `DotProductSimilarity` | The vector indexes' distance function. Akinly's vectors are unit length, so the dot product ranks as cosine does, and the database keeps vectors exactly as written | Only for a connector that does not support the dot product |
| `IndexKind` | null (the connector's default) | The kind of vector index, from `Microsoft.Extensions.VectorData.IndexKind` | `IndexKind.Flat` for exact, reproducible search; `IndexKind.Hnsw` for speed on large stores (with pgvector, see [the filtering caveat](how-to/use-postgresql.md#hnsw-and-filtered-search)) |
| `PageSize` | 100 | The most records one paged read, one lookup by key, or one write sends. Pages of records with vectors are also kept under about 2 MB | Rarely; lower it if a database rejects large requests |
| `SearchOversampling` | 8 | How many more neighbors than asked a nearest-cluster search fetches, to make up for an approximate index | Raise it with an approximate index that misses neighbors |

The collections are created on first use. Changing `Dimensions`, `CollectionPrefix`, `NameSeparator`, `KeyKind`, `DistanceFunction` or `IndexKind` for an existing store points the store at different collections or mismatches the ones there: treat them as fixed once data is written.

## Validation

Invalid options fail early, with every problem listed:

- `new Clusterer(store, options)` throws an `ArgumentException` ("Invalid clustering options: ...").
- `new ChatClientJudge(...)`, `new ChatClientLabeler(...)`, `new VectorDataClusterStore(...)` and `new TextClusterer(...)` throw an `ArgumentException` for theirs.
- With dependency injection, `ClusteringOptions` are validated when the host starts (`ValidateOnStart`): an invalid threshold stops the host with an `OptionsValidationException`, not the first request. The judge's, labeler's and store's options are checked when you call `UseChatClientJudge`, `AddChatClientLabeler` and `UseVectorStore`.

You can check options yourself:

```csharp
var options = new ClusteringOptions { JoinSimilarity = 0.6, CheckSimilarity = 0.7 };
IReadOnlyList<string> problems = options.Validate();
// EmbeddingModel is required: name the model the embeddings come from.
// CheckSimilarity must not exceed JoinSimilarity.
```

The rules for `ClusteringOptions`: `EmbeddingModel` is not empty; every threshold is above 0 and at most 1; `CheckSimilarity` ≤ `JoinSimilarity` and `MergeCheckSimilarity` ≤ `MergeSimilarity`; `MaxChecksPerItem`, `ExamplesPerQuestion` and `MergeExamplesPerCluster` are at least 0, and `MaxOpenQuestions`, `CandidateClusters`, `MaxClustersPerMaintain`, `JoinChoices` and `FailedAnswers.MaxAttempts` at least 1; `FailedAnswers.RetryDelay` is not negative; with `Split.Enabled`, its thresholds are valid, `MinPartSize` is at least 1, and the options have no ask zone.

## Binding from configuration

`AddAkinly(IConfiguration)` binds `ClusteringOptions` from a configuration section, usually `Akinly` in `appsettings.json`. Every property binds by name, nested options included; a `TimeSpan` takes the `hh:mm:ss` form:

```json
{
  "Akinly": {
    "EmbeddingModel": "text-embedding-3-small",
    "JoinSimilarity": 0.83,
    "CheckSimilarity": 0.30,
    "MergeSimilarity": 0.72,
    "MergeCheckSimilarity": 0.55,
    "MaxChecksPerItem": 3,
    "MaxOpenQuestions": 200,
    "CandidateClusters": 10,
    "MaxClustersPerMaintain": 1000,
    "MergeRefusals": "Ask",
    "MergeLinkage": "Average",
    "JoinChoices": 3,
    "ExamplesPerQuestion": 10,
    "MergeExamplesPerCluster": 8,
    "FailedAnswers": {
      "MaxAttempts": 3,
      "RetryDelay": "00:00:01"
    },
    "Split": {
      "Enabled": false,
      "CohesionThreshold": 0.8,
      "RegroupSimilarity": 0.85,
      "MinPartSize": 3
    }
  }
}
```

```csharp
builder.Services.AddAkinly(builder.Configuration.GetSection(ClusteringOptions.SectionName));
```

Leave out any key to keep its default. Environment variables override `appsettings.json` as usual in .NET hosts: `Akinly__JoinSimilarity=0.8`. A second argument adjusts the bound options in code: `AddAkinly(section, o => o.MaxOpenQuestions = 50)`.

The AI and vector store options are set in code, in the `configure` callbacks of their registration methods. To keep them in configuration too, bind them yourself, for example `o => builder.Configuration.GetSection("Akinly:Judge").Bind(o)` inside `UseChatClientJudge`.

## Registration methods

With `Microsoft.Extensions.DependencyInjection`. Every method returns the `AkinlyBuilder`, so calls chain. A complete example is in [Run in ASP.NET Core or a worker service](how-to/dependency-injection.md).

| Method | Package | Registers |
|---|---|---|
| `AddAkinly(configure?)`, `AddAkinly(configuration, configure?)` | `Akinly` | A singleton `Clusterer`, its options (validated at startup), and an `InMemoryClusterStore` unless a store is registered |
| `.UseInMemoryStore()` | `Akinly` | An `InMemoryClusterStore`, one per container |
| `.UseStore<TStore>()`, `.UseStore(store)` | `Akinly` | Your own `IClusterStore` |
| `.UseJudge<TJudge>()`, `.UseJudge(judge)` | `Akinly` | Your own `IClusterJudge`, for `TextClusterer` or your own `SettleAsync` calls |
| `.UseMergePolicy<TPolicy>()`, `.UseMergePolicy(policy)` | `Akinly` | An `IMergePolicy` the `Clusterer` consults before every merge ([how](how-to/merge-policy.md)) |
| `.UseChatClientJudge(configure?, chatClientKey?)` | `Akinly.AI` | A `ChatClientJudge` over the container's `IChatClient`, or a keyed one |
| `.AddChatClientLabeler(configure?, chatClientKey?)` | `Akinly.AI` | A `ChatClientLabeler` over the container's `IChatClient`, or a keyed one |
| `.AddTextClusterer(configure?)` | `Akinly.AI` | A singleton `TextClusterer` over the container's `Clusterer`, `IEmbeddingGenerator<string, Embedding<float>>`, text store, judge (if any) and labeler (if any) |
| `.UseVectorStore(configure, vectorStoreKey?)` | `Akinly.VectorData` | A `VectorDataClusterStore` over the container's `VectorStore`, or a keyed one |

The AI registrations share one `IItemTextStore` for the texts: an `InMemoryItemTextStore` unless you register your own as `IItemTextStore`.
