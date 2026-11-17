# Troubleshooting

Common errors and surprises, what causes them, and how to fix them. Exception messages are quoted as Akinly writes them; none of them ever contains an item's text.

- Errors
  - [EmbeddingModelMismatchException](#embeddingmodelmismatchexception)
  - [ArgumentException when adding items](#argumentexception-when-adding-items)
  - [Options validation errors](#options-validation-errors)
  - [ClusterStoreConflictException](#clusterstoreconflictexception)
  - [A stuck journal](#a-stuck-journal)
  - [A change set too large to journal](#a-change-set-too-large-to-journal)
  - [Qdrant: message too large](#qdrant-message-too-large)
  - [InvalidDataException when importing or reading a store](#invaliddataexception-when-importing-or-reading-a-store)
- Clustering behavior
  - [A judge that keeps failing](#a-judge-that-keeps-failing)
  - [Too many clusters](#too-many-clusters)
  - [Too few clusters](#too-few-clusters)
  - [Items that never get a cluster](#items-that-never-get-a-cluster)
  - [Clusters that should merge but do not](#clusters-that-should-merge-but-do-not)
  - [pgvector with HNSW misses neighbors](#pgvector-with-hnsw-misses-neighbors)
  - [Results differ between runs](#results-differ-between-runs)
  - [Clusters get no labels](#clusters-get-no-labels)

## EmbeddingModelMismatchException

> The store holds embeddings from 'text-embedding-3-small', but the clusterer is configured for 'bge-m3'. Thresholds do not carry over between models; use a separate store.

The store recorded the embedding model on its first add, and this clusterer's `EmbeddingModel` names another. The comparison is exact and case-sensitive: `text-embedding-3-small` and `openai:text-embedding-3-small` are different names.

- If it is the same model under another name, use the name the store holds.
- If you changed models, start a new store (a new `InMemoryClusterStore`, or a new `CollectionPrefix` for a vector database) and add every item again with the new model's embeddings and calibrated thresholds. Vectors from two models cannot be compared, so a store holds one model.
- With `TextClusterer` and an empty `EmbeddingModel`, the name comes from the embedding generator's metadata, which can differ from what you expect. Set `EmbeddingModel` yourself.

> Item 'ticket-7' has an embedding of 768 dimensions where the store holds 1024.

An embedding's length differs from the store's (or from the first item's in the same call). The embedding model or its output size changed: check `defaultModelDimensions` and similar settings, which must stay fixed for a store's lifetime.

> The embeddings have 1536 dimensions, but the store's collections were made for 1024.

From `VectorDataClusterStore`: `VectorDataClusterStoreOptions.Dimensions` does not match the embeddings. Set it to the model's output length. The collections were created with the length they first had, so changing it for an existing prefix does not resize them: use a new `CollectionPrefix`.

## ArgumentException when adding items

| Message | Cause | Fix |
|---|---|---|
| `Item 'x' is already in the store; remove it before adding it again.` | Ids are unique in a store | To replace an item, `RemoveAsync` it first. To make a retry safe, check `GetClusterOfAsync`, or keep track of what was added |
| `Item 'x' appears more than once.` | The same id twice in one call | Deduplicate the batch |
| `An item has an empty id.` / `The list holds a null item.` | A null or empty id, or a null item | Give every item an id |
| `Item 'x' has an embedding that is zero or not finite; it cannot be clustered.` | A zero vector, or one holding a NaN or an infinity | Usually an empty text or a failed embedding call; skip such items or embed them again |
| `A change set moves the store's version up by exactly one.` | From your own `IClusterStore` code or tests calling `ApplyAsync` by hand | The clusterer always sends `expectedVersion + 1`; check your store's version handling |

From `VectorDataClusterStore`, before anything is written: `An item id holds a NUL character or an unpaired surrogate, which databases cannot store.` (and the same for a cluster label). Clean the ids, or map them to safe ones of your own.

## Options validation errors

| Where | What you see |
|---|---|
| `new Clusterer(...)` | `ArgumentException`: `Invalid clustering options: ...`, every problem listed |
| A host with `AddAkinly` | `OptionsValidationException` when the host starts, listing every problem |
| `new ChatClientJudge(...)`, `UseChatClientJudge` | `ArgumentException`: `Invalid judge options: ...` |
| `new ChatClientLabeler(...)`, `AddChatClientLabeler` | `ArgumentException`: `Invalid labeler options: ...` |
| `new VectorDataClusterStore(...)`, `UseVectorStore` | `ArgumentException`: `Invalid vector store options: ...` |

Messages you may meet, and the fix:

- `EmbeddingModel is required: name the model the embeddings come from.` Set `EmbeddingModel`; in configuration, `Akinly:EmbeddingModel`. If the host binds an empty section, check the section name (`Akinly`) and that `appsettings.json` is copied to the output.
- `JoinSimilarity must be above 0 and at most 1.` (and the other thresholds) Thresholds are cosine similarities, such as 0.85, not percentages.
- `CheckSimilarity must not exceed JoinSimilarity.` / `MergeCheckSimilarity must not exceed MergeSimilarity.` The ask zone runs from the check threshold up to the join threshold.
- `Split is for cosine-only setups: ...` Splits are refused with an ask zone. Turn `Split.Enabled` off, or use cosine-only options.
- `Dimensions is required: the length of the embeddings.` Set `VectorDataClusterStoreOptions.Dimensions`.
- `CollectionPrefix must be 1 to 48 letters, digits, underscores and dashes.`

From `TextClusterer`:

- `These options leave close calls to a judge, but no chat client was given. Pass one, or use ClusteringOptions.CosineOnly.` With `chat: null`, the options must have no ask zone.
- `The clusterer leaves close calls to a judge, but none was given.` With dependency injection, `AddTextClusterer` found no `IClusterJudge` while the options have an ask zone: add `UseChatClientJudge()` or `UseJudge(...)`, or use cosine-only options.
- `An IItemTextSource is registered without an IItemTextStore: ...` (`InvalidOperationException`) Register your text store as `IItemTextStore`.

## ClusterStoreConflictException

> The cluster store is at version 42, not 41: another writer changed it.

Every write checks the store's version. When another writer got there first, the clusterer reads again and recomputes, after a short random wait, up to 16 times; the exception means all 16 attempts lost (log event 3, "gave up after 16 conflicting writes").

- **Several clusterers on one `InMemoryClusterStore`** under heavy write load: share one `Clusterer` instead (its calls run one at a time and never conflict with each other), or write less often in larger batches.
- **A `VectorDataClusterStore`** should never see a conflict in normal use: it supports one writer at a time, so a conflict means a second process (or a second store instance over the same collections) is writing. The version check catches a writer that came first but cannot stop two from racing, which can leave the data inconsistent. Run one writer. See [One writer](concepts.md#one-writer).
- **Your own store:** check that `ApplyAsync` compares the version atomically with the write.

## A stuck journal

> The store's journal holds change set 1234, which could not be applied: NpgsqlException. Fix the cause and retry; until then every write stops here, so nothing is computed from half-written data.

`VectorDataClusterStore` journals each change set before writing it. When a write stops halfway (a crash, a lost connection), the next write finishes the journaled one first. If finishing it fails too, every write stops with this `InvalidOperationException`, and the exception type of the cause is named in the message and attached as the inner exception.

- **Fix the cause** (the database unreachable, out of disk space, a missing permission, an expired credential), then write again: the next call finishes the journal and carries on. Nothing needs cleaning up.
- Reads keep working meanwhile, and can see part of the stopped change set.
- The store checks every change set for what a database could not store before journaling it, so the change set itself should never be the cause. If it ever is, deleting the records in the `{prefix}_meta` collection whose `Name` is `journal` or starts with `journal` drops the journal. The part of that change set already written stays, and the store's version stays at the old one: a last resort, best done on a copy of the data.

Related errors (`InvalidDataException`): `Part N of the store's journal is missing.` or `The store's journal parts belong to another change set than its header.` mean the journal was changed outside Akinly. `The store's journal was written by a newer Akinly ...` means a newer version of Akinly wrote to this store: upgrade.

## A change set too large to journal

> The change set is too large to journal (... items and ... clusters of 3,072 dimensions): add fewer items per call.

One `AddAsync` (or `MaintainAsync`) produced a change set beyond the journal's limit, which takes some hundreds of thousands of long vectors. Nothing was written. Add the items in smaller batches.

## Qdrant: message too large

A gRPC error (`RpcException` with status `ResourceExhausted`) saying a message is larger than the maximum size, while adding items to a `VectorDataClusterStore` on Qdrant.

Qdrant's .NET client limits a message to 4 MB by default. Akinly keeps its own paged reads and writes under about 2 MB. A nearest-cluster search is not paged: it asks for `CandidateClusters` (10) plus `SearchOversampling` (8) clusters, plus one for every cluster the current call has already created or changed, and each comes back with its centroid and its sum. With long vectors, a large `AddAsync` that touches hundreds of clusters can make that answer pass the limit.

- **Add fewer items per call**, such as a few hundred: each call starts with no changed clusters.
- With `TextClusterer`, call `AddAsync` with smaller batches; `EmbeddingBatchSize` only splits the embedding requests.
- Keep `CandidateClusters` and `SearchOversampling` at their defaults unless you need more.

## InvalidDataException when importing or reading a store

From `InMemoryClusterStore.ImportAsync`: `The snapshot is not a cluster store written by ExportAsync.`, `The snapshot is empty.`, `The snapshot has schema version N; this version of Akinly reads up to M.`, or a message about a part that contradicts another (counts, redirects, item states). The file is damaged, was edited, or comes from a newer Akinly. Restore a good copy, or upgrade. [Save and restore](how-to/save-and-restore.md#save-and-load) shows how to write the file safely.

From `VectorDataClusterStore`: `The store was written by a newer Akinly (schema N); ...`. Upgrade Akinly; a store is never misread by an older version.

## A judge that keeps failing

Symptoms: items are placed slowly, `akinly.verdicts` with `kind=failed` climbs, and the logs show warnings 101 ("could not be read") or 102 ("the request ... failed with {ExceptionType} (status {StatusCode})"). After `FailedAnswers.MaxAttempts` (3) failures a join is decided by similarity alone, so a long outage leaves more, smaller clusters, and merges wait. Failures are never recorded as refusals, so nothing is lost for good: fix the cause, and later items and merge checks are judged again.

| Cause | Sign | Fix |
|---|---|---|
| The model server is unreachable or the model name is wrong | 102 with `HttpRequestException`, often status 404 | Check the address and the model name (`ollama list`; the provider's model id); for one client serving several models, set `ChatOptions.ModelId` |
| Requests time out | 102 with `TaskCanceledException` or `OperationCanceledException` | Raise `RequestTimeout` (default 2 minutes) or lower `MaxParallelRequests` (default 4), especially for a local model |
| Rate limits | 102 with status 429 | Lower `MaxParallelRequests`; raise `FailedAnswers.RetryDelay` |
| The provider rejects response schemas or temperature | 102 with status 400 | Set `UseStructuredOutput = false`, or leave `UseZeroTemperature` off (its default) and set no `Temperature` in `ChatOptions`: current Claude models and OpenAI's reasoning models reject any temperature |
| The answers cannot be read | 101 | The model ignores the answer format, or runs out of output tokens while thinking. Use a model that follows instructions, turn thinking off ([Ollama](how-to/use-ollama.md#the-program)), or raise the output token limit in `ChatOptions` |
| A judge of your own throws | The exception reaches your `SettleAsync` call | Return `VerdictKind.Failed` for a failed question instead of throwing |

`SettleAsync` waits `FailedAnswers.RetryDelay` (1 second) after a round with a failed answer, and doubles the wait, up to 5 minutes, while every answer fails, so a rate limit has time to pass and an outage does not spin. When you call `AnswerAsync` yourself, pass `Verdict.Failed` for a question you could not answer, and ask it again later.

## Too many clusters

Most clusters hold one item, and items that say the same thing sit apart.

- **Thresholds too high for the embedding model.** The defaults were calibrated for `text-embedding-3-small`; another model may put matching texts at lower similarities. [Calibrate](how-to/calibrate-thresholds.md).
- **The judge refuses too much.** Its `Criterion` may be stricter than you mean; the default asks for "the same specific problem or request, so that one answer or fix would settle both". Loosen it for your data, or set `Preference = ClusteringPreference.Recall` with the matching thresholds from the calibration report.
- **Few questions per item.** An item stops asking after `MaxChecksPerItem` (3) refusals, and a "none" to a question with choices counts one for each cluster offered. Raise it if items often belong to a cluster further down their nearest.
- **A failing judge.** Questions that fail every attempt are decided by similarity, which joins only at the join threshold. See [A judge that keeps failing](#a-judge-that-keeps-failing).
- **No maintenance.** Merges happen only in `MaintainAsync`. With a `Clusterer`, call it after each batch, and more than once after a large one (each cluster merges at most once per call). `TextClusterer` runs it after every add unless `MaintainAfterAdd` is off.
- **Splits in a cosine-only setup** with a high `Split.CohesionThreshold` cut clusters apart. Lower it or turn splits off.

## Too few clusters

A few clusters hold most items, and they mix different things.

- **Thresholds too low for the embedding model.** Raise `JoinSimilarity` and `MergeSimilarity`, or calibrate.
- **Cosine only on data where topics blur together.** No single threshold separates "the same topic" from "the same thing": add a judge for the band between them.
- **The judge accepts too much.** Make `Criterion` more specific ("describing the same bug", not "about the same feature"), or set `Preference = ClusteringPreference.Precision` with the matching thresholds from the calibration report.
- **`MergeRefusals = Ignore`** lets merges carry items into clusters that refused them without asking. Keep the default, `Ask`.
- **Very uniform data**, where every item sounds alike, grows broad clusters even with a judge. Labels help you see it; a stricter criterion, or splitting the stream by a field you already have (product, category), helps more.

## Items that never get a cluster

`GetClusterOfAsync` returns null for an item you added.

- **Its question is open.** With a `Clusterer`, nothing answers questions on its own: answer them (`AnswerAsync`, `SettleAsync`), see [Answer questions](how-to/answer-questions-yourself.md). `GetOpenQuestionsAsync` lists them.
- **It waits for room to ask.** At most `MaxOpenQuestions` (200) questions are open; more items wait, and are placed by the next call that makes room. Answer the open questions, or raise the limit.
- **It was removed**, or never added (the call threw).

## Clusters that should merge but do not

- `MaintainAsync` has not run since they became close, or has not reached them yet: one call checks at most `MaxClustersPerMaintain` (1,000) changed clusters, and merges each cluster at most once.
- A judge called the pair different, or a split separated them; such pairs stay apart for good.
- One cluster refused an item now in the other, and `MergeRefusals` is `Block`. With `Ask`, the default, the pair is asked about; a judge may have called it different.
- Their similarity (the average similarity of their members, `MergeLinkage`) is below `MergeCheckSimilarity` (0.55), or below `MergeSimilarity` (0.72) with no judge.
- Your `IMergePolicy` answers `Never` for the pair.
- Each is outside the other's `CandidateClusters` (10) nearest neighbors.
- `MaxOpenQuestions` was reached, so the merge question waits for a later `MaintainAsync`.
- One of them is empty: empty clusters are never merged.

## pgvector with HNSW misses neighbors

With `IndexKind.Hnsw` on PostgreSQL, items start new clusters where they should join, or examples come back short. pgvector filters after the index scan, and Akinly's searches are filtered. Turn on iterative scans (`hnsw.iterative_scan`, pgvector 0.8 or later), raise `hnsw.ef_search`, or use the default exact search. Details: [HNSW and filtered search](how-to/use-postgresql.md#hnsw-and-filtered-search).

## Results differ between runs

- **Input order.** Items are placed one at a time, so a different arrival order can give different clusters. That is inherent in incremental clustering.
- **An approximate index** (Qdrant's HNSW, pgvector with `IndexKind.Hnsw`) can return slightly different candidates. For reproducible results, use the in-memory store or an exact index (`IndexKind.Flat` with pgvector).
- **A model judge** can answer a close call differently from run to run. A temperature of 0 (`UseZeroTemperature`, for models that accept one) makes it steadier, but not identical across runs, versions or hardware.

## Clusters get no labels

- A cluster needs `MinClusterSize` (3) members.
- `TextClusterer` labels only with a chat client and `LabelClusters` on, and only the clusters an add touched; `ChatClientLabeler.LabelAllAsync` labels every cluster that is due.
- The model's labels are rejected (log event 104): too long, more than one line, or holding braces, double quotes or backticks. A thinking model's reasoning in the answer makes it more than one line: turn thinking off for the labeler.
- No texts: the labeler reads the members' texts from the `IItemTextSource`; after a restart with an in-memory text store, there are none.
