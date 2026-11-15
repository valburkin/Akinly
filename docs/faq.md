# FAQ

- [Is it a replacement for HDBSCAN or k-means?](#is-it-a-replacement-for-hdbscan-or-k-means)
- [Does it send my texts anywhere?](#does-it-send-my-texts-anywhere)
- [Which models can I use?](#which-models-can-i-use)
- [How many questions does an item cost?](#how-many-questions-does-an-item-cost)
- [Can several processes write to one store?](#can-several-processes-write-to-one-store)
- [Is it thread-safe?](#is-it-thread-safe)
- [Can I change the thresholds later?](#can-i-change-the-thresholds-later)
- [Can I change the embedding model later?](#can-i-change-the-embedding-model-later)
- [How do I update an item?](#how-do-i-update-an-item)
- [How large can a store get?](#how-large-can-a-store-get)
- [Are the results reproducible?](#are-the-results-reproducible)

## Is it a replacement for HDBSCAN or k-means?

No; it answers a different question. Batch algorithms such as k-means, agglomerative clustering or HDBSCAN see all items at once and return a fresh grouping on every run, which suits a one-off analysis of a fixed corpus ("find the 20 themes in this spreadsheet"). Akinly is for a stream: items arrive over days or months, each is placed as it comes, and clusters keep ids you can store and link to. If you only ever cluster one fixed set, a batch algorithm may serve you better.

## Does it send my texts anywhere?

The core package (`Akinly`) never sees a text: it works on ids and embeddings.

`Akinly.AI` sends texts only to the models you give it: the embedding generator (each text, to embed it) and the chat client (a judge's prompt quotes the item and up to `ExamplesPerQuestion` members of each cluster offered, cut at `MaxTextLength` characters; a labeler's prompt quotes up to `ExamplesPerLabel` members). With a local model, nothing leaves your machine. Texts are kept in the `IItemTextStore` you choose, in memory by default.

No exception, log entry, trace or metric that Akinly writes carries an item's text. The stores keep ids, embeddings and labels. Two cautions: much of a text can be recovered from its embedding, so protect the store like the texts; and a label is written from members' texts, so review labels before showing them publicly.

## Which models can I use?

Any embedding model behind `IEmbeddingGenerator<string, Embedding<float>>` and any chat model behind `IChatClient`, from Microsoft.Extensions.AI: Ollama through OllamaSharp, OpenAI and Azure OpenAI through Microsoft.Extensions.AI.OpenAI, and any other provider with an adapter. The repository's end-to-end test runs against Ollama with `bge-m3` and `qwen3.5:9b`, and the evaluation tool supports Ollama and OpenAI models, and Anthropic models as judges.

The judge needs a chat model that follows a short instruction and answers with a small JSON object: `{"same": true}` or `{"same": false}` for a yes-or-no question, and `{"group": n}` for a join question with choices (the number of the group the item belongs in, 0 for none). Small, fast models are the natural choice, since it answers many short questions. The default thresholds were calibrated for OpenAI's `text-embedding-3-small`; for any other embedding model, [calibrate them](how-to/calibrate-thresholds.md). See the [evaluation results](evaluation/README.md) for measured setups.

## How many questions does an item cost?

Only items in the ask zone cost anything, and each is bounded:

- **Joins.** A join question offers up to `JoinChoices` (3) of the item's nearest clusters, and the judge picks one or none. A "none" counts as a refusal by each cluster offered, and an item stops asking after `MaxChecksPerItem` (5) refusals: a question never offers more clusters than the item has checks left, so with the defaults an item asks twice (three clusters, then two) when it has that many neighbors, and at most five times when each question offers one. A failed answer is asked again, up to `FailedAnswers.MaxAttempts` (3) times per question.
- **Merges.** At most one question per pair of clusters in the merge-check zone, asked when one of them has changed; a pair answered once is not asked again.
- **Items in the join or new zone** cost no question at all.

How many items fall in the ask zone depends on your data, the embedding model and the thresholds. The default ask zone is wide, since the judge decides the close calls; to ask less, raise `CheckSimilarity` or lower `MaxChecksPerItem`. The [evaluation results](evaluation/README.md) report the judge's input tokens and cost per 1,000 texts on public datasets, and the `akinly.questions.asked` and `akinly.items.placed` counters ([telemetry](how-to/opentelemetry.md)) show yours.

## Can several processes write to one store?

It depends on the store:

- **`InMemoryClusterStore`** lives in one process; several `Clusterer` instances in it can share it safely, since its writes compare and swap the version.
- **`VectorDataClusterStore`** supports **one writer at a time**. Vector databases offer neither transactions across collections nor compare-and-swap, so two writing processes can race. Run the clusterer in one process (or behind a lock of your own); any number of processes can read. A write stopped halfway is finished by the next write, from any process.
- **Your own store** over a transactional database can offer more: make `ApplyAsync` compare the version and write in one transaction, and throw `ClusterStoreConflictException` when the version moved. The clusterer then retries conflicts on its own.

See [One writer](concepts.md#one-writer).

## Is it thread-safe?

- **`Clusterer`** and **`TextClusterer`**: yes. Calls on one instance run one at a time, so share a single instance (dependency injection registers singletons). The read methods do not wait for writes.
- **`InMemoryClusterStore`** and **`InMemoryItemTextStore`**: yes.
- **`ChatClientJudge`**: yes; it sends up to `MaxParallelRequests` questions at once itself.
- **Your own `IClusterJudge`**: must be safe to call from several threads if you share it.
- **Options objects** are copied when a clusterer, judge, labeler or store is built, so changing them afterwards has no effect.

## Can I change the thresholds later?

Yes. The store records only the embedding model, not the thresholds. New options apply to every decision from then on; existing clusters stay as they are, and a lower merge threshold takes effect as `MaintainAsync` reaches the clusters that change.

## Can I change the embedding model later?

Not within a store. Vectors from two models cannot be compared, so a store holds one model, and a clusterer configured for another refuses to work on it (`EmbeddingModelMismatchException`). To switch, start a new store and add every item again with the new model's embeddings and thresholds calibrated for it. Cluster ids in the new store are new.

## How do I update an item?

Remove it and add it again: `RemoveAsync([id])`, then `AddAsync` with the new embedding. The item may land in another cluster. `AddAsync` refuses an id that is already in the store, so a retried add cannot place an item twice.

## How large can a store get?

- **`InMemoryClusterStore`** keeps everything in memory and finds nearest clusters by scanning every cluster, so it is meant for up to tens of thousands of clusters. It suits tests, jobs that fit in memory, and stores you snapshot as JSON. `benchmarks/Akinly.Benchmarks` (BenchmarkDotNet) measures search and add times by store size; run it on your own hardware with `dotnet run -c Release --project benchmarks/Akinly.Benchmarks -- --filter '*'`.
- **`VectorDataClusterStore`** uses the database's own vector index for nearest-cluster search and pages every read, so it grows with the database.

## Are the results reproducible?

The same calls in the same order, with the same answers, give the same clusters, when nearest-cluster search is exact (the in-memory store, or a database with an exact index). An approximate index can return slightly different candidates; a different input order can give different clusters; and a model judge can answer a close call differently from run to run (a temperature of 0, for models that accept one, makes it steadier, not identical). `akinly-eval evaluate` can feed items in several orders and report the spread; the published results use one order per setup.
