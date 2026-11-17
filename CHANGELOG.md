# Changelog

All notable changes to Akinly are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/). Breaking changes to the public API come only in a major version.

## [Unreleased]

## [1.1.0] - 2026-11-17

**Upgrading from 1.0.** The API is compatible, but clustering changes without a change to your code: merge thresholds are read as the average similarity of the clusters' members, refused pairs go to a judge, an item takes at most three refusals, and a merge question is put to a chat model as two join requests. Merge thresholds you set for 1.0 were measured on centroids. To keep 1.0's behavior, set `MergeLinkage = Centroid`, `MergeSimilarity = 0.96`, `MergeCheckSimilarity = 0.48`, `MergeRefusals = Block`, `MaxChecksPerItem = 5` and `ChatClientJudgeOptions.MergeMembersEachWay = 0`. `MergeQuestion.Similarity` is measured as `MergeLinkage` says, so a judge of your own that reads it sees average similarities. A host that builds prompts with `JudgePrompts.BuildAsync` still asks a merge as one group-against-group question; `BuildRequestsAsync` builds what `ChatClientJudge` now sends.

### Added

- `ThresholdCalibrator.CalibrateAsync` finds where the ask zones should start for your data, from your judge's own answers: it clusters a sample in a throwaway store with wide ask zones, counts how often the judge says yes in each band of similarity (`ThresholdCalibration.JoinBands` and `MergeBands`), and suggests `CheckSimilarity` and `MergeCheckSimilarity` where the yes-rate rises to a target (`CalibrationOptions.JoinYesRate`, `MergeYesRate`). `ThresholdCalibration.ApplyTo` copies options with the suggested floors. It is an explicit step: thresholds still never shift while a clusterer runs.
- `ClusteringOptions.MergeLinkage` (`ClusterLinkage`): how the similarity of two clusters is measured for merging. `Average` (the default) is the average similarity of a member of one to a member of the other; `Centroid` is the similarity of their centroids, as in 1.0. A centroid averages away what sets a cluster's members apart, so on texts written to one template two mixed clusters look alike by their centroids though their members do not: on a realistic set, two clusters of 50 unrelated statements scored 0.98 by their centroids, above the merge threshold, and 0.47 on average. Merges then snowballed into a few catch-all clusters.
- `ChatClientJudgeOptions.MergeMembersEachWay` (default 1): a merge question goes to the model as yes-or-no join questions, whether each cluster's most central member belongs in the other, and the clusters merge only if every answer is yes. A model places one text in a group far more reliably than it compares two groups. 0 asks the 1.0 question, the two groups as a whole.
- `JudgePrompts.BuildRequestsAsync`, `JudgePrompts.ReadVerdict` and `JudgePrompts.ReadAnswer`, for hosts that send the requests themselves (a provider's batch API, a queue): every request a question needs, the way `ChatClientJudge` asks it, in a fixed order; the verdict from their answers in any order, given how many requests there were, so a host may send them all at once and a request that never came back is never taken for a yes; and each answer on its own, so a host may send them one after another and stop at the first no, as `ChatClientJudge` does.
- `ClusteringOptions.MergeRefusals` (`RefusalRule`): how a refusal bears on merging two clusters. `Ask` (the default) leaves the pair to a judge and never merges it on similarity alone; `Block` never merges it, as 1.0 did; `Ignore` merges it like any other pair.
- `akinly-eval` (not shipped): `prepare-complaints` writes a realistic set of one-sentence need statements on one template from public CFPB complaint narratives (dataset `complaints`); `experiment` reports coverage, near-duplicate clusters and why they stayed apart, the judge's yes-rate by similarity, and, with `--referee`, whether each cluster's title fits a sample of its members, by how they were placed; `--calibrate-floors` sets the floors with `ThresholdCalibrator`; `--merge-linkage` and `--merge-members` choose how merges are measured and asked. `calibrate` measures merges with the average linkage.

### Changed

- New defaults: `MaxChecksPerItem` 3 (was 5; with three choices, one question per item), `MergeSimilarity` 0.72 and `MergeCheckSimilarity` 0.55 on the average linkage (were 0.96 and 0.48 on centroids), and merges asked by members; `JoinSimilarity` (0.83) and `CheckSimilarity` (0.30) are unchanged. Measured on 1,500-item development samples with OpenAI `text-embedding-3-small` and a local judge (gemma4:12b), one run each in one order:
  - On a realistic set of need statements written from public complaints, graded by another local model (gemma4:26b) on whether each item fits its cluster's title, 90% of items fit with the new defaults and 77% with 1.0's. Items moved by a merge fit 79% of the time, against 49%. 79% of items are in clusters of three or more, against 88%.
  - Adjusted Rand index on Banking77 0.533 (1.0: 0.377) and CLINC150 0.742 (0.725), but MASSIVE 0.276 (0.337): with fewer merges, MASSIVE's many loosely worded requests stay in more, smaller clusters.
  - On texts written to one template, such as statements extracted from longer texts, a judge rarely says yes at low similarity (on the complaints set, to 2 of 77 join questions below 0.60): raise `CheckSimilarity` for such data, to where `ThresholdCalibrator` puts it (about 0.60 on such data with `text-embedding-3-small`).

  Merge thresholds you set yourself were measured on centroids: recalibrate them, or set `MergeLinkage = Centroid` to keep them. The average linkage reads clusters' sums (`StoredCluster.Sum` and `Count`), not their centroids: a store or a test that sets a cluster's centroid without its sum no longer changes how it merges.
- Recalibrated example thresholds for the average linkage and three choices per question: `qwen3-embedding:8b` 0.96 / 0.57 / 1.00 / 0.75 (no merge was safe without a judge) and `bge-m3` 0.96 / 0.54 / 0.88 / 0.75 (join / check / merge / merge check), in the guides and package READMEs.
- A refusal no longer keeps two clusters apart for good. In 1.0, a "none" to a join question with choices counted as a refusal by every cluster offered, and any refusal between two clusters blocked their merge, so on texts written to one template near-identical clusters were never even asked about (on 3,000 statements of the complaints set, 86 of 127 pairs of clusters with centroids at 0.88 or more). With `MergeRefusals = Ask`, a judge decides.

### Deprecated

- `ClusteringOptions.MergeRespectsRefusals`: use `MergeRefusals`. Setting it still works: `true` is `RefusalRule.Block`, `false` is `RefusalRule.Ignore`.

## [1.0.2] - 2026-11-15

### Fixed

- The packages required the Microsoft.Extensions packages 11.0.0, so a .NET 10 app that pins the 10.0 packages could not add them. They now ask for the oldest versions they work with: 10.0.0 for the core's Microsoft.Extensions packages and System.Numerics.Tensors, and 10.6.0 for Microsoft.Extensions.AI.Abstractions.

## [1.0.1] - 2026-11-15

### Added

- A package icon on nuget.org (`assets/icon.svg`, original artwork under the repository's MIT license).

## [1.0.0] - 2026-11-15

### Added

- `Clusterer`: incremental clustering kept in an `IClusterStore`. `AddAsync` places items (join, ask a judge, or start a cluster), `AnswerAsync` applies a judge's verdicts whenever they arrive, `MaintainAsync` merges clusters that turn out to be one (and splits loose ones in cosine-only setups), `RemoveAsync` takes items out, and `SetLabelAsync` labels clusters. Close calls come back as `JoinQuestion` and `MergeQuestion` records with everything a judge needs but the texts, so they can be answered live, in a provider's batch, or by a person.
- `SettleAsync` loops add, judge and answer for callers that answer live, waiting `FailedAnswers.RetryDelay` after a round with a failed answer (doubled, up to 5 minutes, while every answer fails); `DelegateJudge` wraps a function as a judge, returning a `VerdictKind` or a whole `Verdict` (to pick among a question's choices), and turns the function's exceptions into failed answers. An optional `IMergePolicy` (or `DelegateMergePolicy`, `UseMergePolicy` in dependency injection) keeps chosen pairs of clusters apart and chooses which keeps its id when two merge. Verdicts are `Same`, `Different`, `Failed` (asked again) or `Unanswerable` (similarity decides).
- Join questions with choices: a join question offers up to `ClusteringOptions.JoinChoices` (default 3) of the item's nearest clusters with members, nearest first (`JoinQuestion.OtherChoices`, a list of `JoinChoice`). The nearest must be at or above `CheckSimilarity`; the others come as context, whatever their similarity, so the judge sees how fine the clusters are. The judge answers with `Verdict.Picked(questionId, clusterId)` (`Verdict.PickedClusterId`), `Verdict.Same` for the nearest, or `Verdict.Different` for none, which counts as a refusal by every cluster offered. A pick that was not offered counts as a failed answer, and `SettleAsync` passes a judge's pick through. `JoinChoices = 1` asks yes or no.
- `IItemTextSource`, `IItemTextStore` and `InMemoryItemTextStore`: where a judge and a labeler read items' texts, and where they are kept; the clusterer itself never keeps text.
- `ClusteringOptions.Clone` for a deep copy.
- `ClusteringOptions` with defaults calibrated for OpenAI's `text-embedding-3-small` (join 0.83, check 0.30, merge 0.96, merge check 0.48; reproducible with `akinly-eval calibrate --dataset intents --embedder openai:text-embedding-3-small --sample 6000`), wide ask zones so the judge decides the close calls, at most `MaxChecksPerItem` (5) refusals per item, validation, and a `CosineOnly` preset; failed answers are retried and never count as refusals; refusals follow merges, so an item never ends up in a cluster that refused it; a pair a judge called different, or that a split separated, stays apart.
- `IClusterStore`, its records and change sets, and `InMemoryClusterStore`, with optimistic concurrency, exact nearest-cluster search, and JSON export and import (validated on import). Centroids come from exact sums, so removals and merges keep them true.
- `AddAkinly` for dependency injection, with options bound from configuration and validated at startup.
- Traces and metrics under the name `Akinly`, and source-generated log messages; none carries an item's text.
- `IncrementalClusterer`: incremental clustering with lasting cluster ids. `Assign` places items in the nearest cluster at or above a threshold or starts new ones, `FindMerges` finds clusters close enough to be one (over every pair, or over candidate pairs from a vector index), `TrySplit` regroups a loose cluster, and `Cohesion` measures how tight one is.
- Judged joins: `ClusterToCheck` returns the cluster an item should be checked against when its similarity falls between the check and join thresholds, `Join` adds an item a judge accepted (never a zero or non-finite embedding), and `Assign` skips clusters that refused an item.
- `Vectors`: normalization, dot product and means in double precision, for callers that keep clusters themselves.
- Input checks on every entry point: thresholds above 0 and at most 1, unique ids, counts and limits within range, and vectors of one length per call. A bad input throws an `ArgumentException` naming the id or size, never content.
- `Akinly.AI`, a package for any model behind Microsoft.Extensions.AI:
  - `ChatClientJudge` decides close calls with any `IChatClient` (a local Ollama model or a cloud one), several at once, with a JSON answer through structured output. Its requests carry no tools or conversation state, texts are fenced as JSON data, answers after a model's reasoning are read correctly, and every failure (the model, a timeout, the text store) becomes a `Failed` verdict; a question with nothing fair to show is `Unanswerable`.
  - Join questions with choices get their own prompt (`ChoiceInstructions`, default `JudgePrompts.DefaultChoiceInstructions`) and answer, `{"group": n}` with 0 for none (`JudgePrompts.ChoiceAnswerSchema`).
  - `ChatClientJudgeOptions.Preference` (`ClusteringPreference`: `Balanced`, the default, `Precision` or `Recall`) adds a tie-break to the prompts: when unsure, keep apart, or put together.
  - `JudgePrompts` builds the same prompts and request options and reads the answers, for a provider's batch API (`BuildChoice`, `TryParseChoice` and `CreateRequestOptions(options, choice)` for questions with choices).
  - `ChatClientLabeler` titles clusters once they reach a size and again when they double or halve; labels that could carry instructions are rejected; `Cluster.LabeledAtCount` records the size at labeling.
  - `TextClusterer`: texts in, clusters out, embedding with any `IEmbeddingGenerator`, judging, merging and labeling in one call, one call at a time, with the texts in any `IItemTextStore`.
  - `UseChatClientJudge`, `AddChatClientLabeler` and `AddTextClusterer` for dependency injection, with keyed chat clients for a different model per role.
- `Akinly.VectorData`, a package that keeps the clusters in any vector database with a Microsoft.Extensions.VectorData connector (tested against Qdrant and PostgreSQL with pgvector):
  - `VectorDataClusterStore` searches the database's own vector index, maps ids to UUID or text keys, pages by key within a byte budget, and supports one writer at a time. Each change set is checked for anything a database could not store, then journaled before it is written, so the next write finishes one that was stopped halfway; reads can see a write in progress. Any connector whose database shows writes to the next read at once and supports filtered, ordered reads should work.
  - `UseVectorStore` for dependency injection, with a keyed `VectorStore` if you have several.
  - It depends on Microsoft.Extensions.VectorData.Abstractions 10.1.0, the version current connectors are built against; they fail against 10.5.0 and later, which removed types they use.
- `akinly-eval` (`tools/Akinly.Evaluation`, not shipped): `calibrate` suggests thresholds for an embedding model by replaying labeled texts the way the clusterer sees them (merges on groups cut into a fragment of one to five items and the rest; ask zones that keep all but 1% of what a judge could reach), with a set for each `ClusteringPreference`; `evaluate` clusters the test split of a public dataset (or your own labeled texts) cosine-only and with a judge, at thresholds calibrated on the training split, in several input orders, and reports adjusted Rand index, NMI, pairwise precision, recall and F1, questions and tokens per item, and failed answers. Datasets are downloaded from a fixed commit and checked against their hash, never committed.
- The evaluation tool's datasets: Banking77 (CC BY 4.0), CLINC150 (CC BY 3.0) and MASSIVE (English, CC BY 4.0) intents, `intents` (the three intent sets pooled, labels kept apart), News-T and Tweets-T (short-text stream sets of Yin et al., 2018; no license stated, downloaded for evaluation only and never redistributed). News-T and Tweets-T have no split, so both splits are the whole set.
- Documentation: a README with a quick start for each setup, guides in `docs/` (getting started, concepts, configuration, troubleshooting, FAQ, and how-to guides for Ollama, OpenAI, Azure OpenAI and Anthropic, cosine-only clustering, answering questions yourself or through a batch API, merge policies, Qdrant, PostgreSQL with pgvector, labels, removals, calibration, dependency injection, OpenTelemetry, and saving the in-memory store), and a README for each package. The examples that need only the core package run as tests (`ReadmeExampleTests`, `DocsExampleTests`).
- Benchmarks (`benchmarks/Akinly.Benchmarks`, BenchmarkDotNet) of nearest-cluster search, adding items and maintenance, by store size, on data where items join and clusters merge.

[Unreleased]: https://github.com/valburkin/Akinly/compare/v1.1.0...HEAD
[1.1.0]: https://github.com/valburkin/Akinly/compare/v1.0.2...v1.1.0
[1.0.2]: https://github.com/valburkin/Akinly/compare/v1.0.1...v1.0.2
[1.0.1]: https://github.com/valburkin/Akinly/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/valburkin/Akinly/releases/tag/v1.0.0
