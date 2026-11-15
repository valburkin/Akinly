# Calibrate thresholds for your embedding model

Akinly's four thresholds are cosine similarities, and every embedding model spreads similarities differently: two texts that one model puts at 0.80 another may put at 0.65. The defaults (join 0.83, check 0.30, merge 0.96, merge check 0.48) were calibrated with this tool for OpenAI's `text-embedding-3-small` at its default dimensions, on 6,000 texts pooled from the training splits of Banking77, CLINC150 and MASSIVE (287 groups). With any other model, calibrate them before you trust the clusters.

The ask zones the defaults leave (0.30 to 0.83 for joins, 0.48 to 0.96 for merges) are wide on purpose: the judge, not similarity, decides the close calls, and `MaxChecksPerItem` bounds the questions per item. Only joins and merges without a question have to be nearly always right, so the join and merge thresholds are high, and the check thresholds low enough that the judge sees almost every item it could place.

`akinly-eval calibrate` suggests all four from labeled texts, by replaying them the way the clusterer sees them. The tool lives in the repository (`tools/Akinly.Evaluation`) and is not shipped as a package; run it from a clone.

## Calibrated values

These were calibrated the same way as the defaults (`calibrate --dataset intents --sample 6000`, balanced). They suit short requests and complaints in English; for other kinds of text, calibrate on your own.

| Embedding model | `JoinSimilarity` | `CheckSimilarity` | `MergeSimilarity` | `MergeCheckSimilarity` |
|---|---|---|---|---|
| OpenAI `text-embedding-3-small` (the defaults) | 0.83 | 0.30 | 0.96 | 0.48 |
| `qwen3-embedding:8b` (Ollama) | 0.96 | 0.55 | 0.97 | 0.71 |
| `bge-m3` (Ollama) | 0.96 | 0.52 | 0.985 | 0.66 |

## 1. Prepare labeled texts

A JSON-lines file, one text per line, each with an id, the text and a label that names its group (texts with the same label say the same thing):

```json
{"id": "1", "text": "The heating in my apartment has been broken for two weeks.", "label": "heating"}
{"id": "2", "text": "There is no heat in our unit and it is freezing.", "label": "heating"}
{"id": "3", "text": "My landlord raised the rent by 30 percent this year.", "label": "rent-increase"}
```

- Use texts like the ones you will cluster, from the same source, labeled at the grain you want clusters at: "duplicate charge" and "refund not received" as two labels if they should be two clusters.
- At least 100 texts are needed; a few thousand in many groups give steadier numbers. By default the tool samples up to 3,000, keeping each label's share.
- Every field must be a non-empty string, and ids must be unique.

No labeled data of your own? The tool also calibrates on public datasets, downloaded on first use from a fixed commit, checked against their SHA-256, and never committed:

| Name | What it is | License |
|---|---|---|
| `banking77` | Customer-service requests to a bank, 77 intents (PolyAI) | CC BY 4.0 |
| `clinc150` | Requests to a virtual assistant, 150 intents (Larson et al., 2019); the out-of-scope queries are left out | CC BY 3.0 |
| `massive` | MASSIVE's English requests to a virtual assistant, 60 intents, 59 of them in the test split (Amazon, FitzGerald et al., 2022) | CC BY 4.0 |
| `intents` | The three sets above pooled, each label kept apart (287 groups in the training splits); the library's defaults come from it | As above |
| `news-t` | News titles in labeled groups, a short-text stream set (Yin et al., 2018) | None stated: downloaded for evaluation only and never redistributed |
| `tweets-t` | Tweets in labeled groups, a short-text stream set (Yin et al., 2018) | None stated: downloaded for evaluation only and never redistributed |

`calibrate` reads a dataset's training split. `news-t` and `tweets-t` have no split, so both splits are the whole set. The public sets show how a model spreads similarities on short texts, but thresholds fit to your own data are better.

## 2. Run it

From the repository root, with your embedding model:

```bash
# A local Ollama model (OLLAMA_HOST, default http://localhost:11434)
dotnet run -c Release --project tools/Akinly.Evaluation -- calibrate --dataset my-texts.jsonl --embedder ollama:bge-m3

# An OpenAI model (OPENAI_API_KEY must be set)
dotnet run -c Release --project tools/Akinly.Evaluation -- calibrate --dataset my-texts.jsonl --embedder openai:text-embedding-3-small

# A public dataset
dotnet run -c Release --project tools/Akinly.Evaluation -- calibrate --dataset banking77 --embedder ollama:bge-m3
```

| Option | Default | What it does |
|---|---|---|
| `--dataset` | `banking77` | `banking77`, `clinc150`, `massive`, `intents`, `news-t`, `tweets-t`, or the path to your JSON-lines file |
| `--embedder` | `ollama:bge-m3` | The embedding model, as `provider:model`; the providers are `ollama` (a local Ollama at `OLLAMA_HOST`) and `openai` (`OPENAI_API_KEY`). Anthropic has no embedding models |
| `--sample` | 3000 | How many texts to calibrate on (0 for all of them) |
| `--loss` | 0.01 | The share of the items (and merge pairs) a judge could reach that the ask zones may leave out |
| `--max-checks` | 5 | How many nearest clusters an item's checks reach, `MaxChecksPerItem` |
| `--seed` | 1 | The random seed for the sample and the replay order; the same seed gives the same result |
| `--cache` | `.eval-cache` | Where downloads and embeddings are kept, so a second run embeds nothing again |
| `--out` | `docs/evaluation` | Where the report is written |

The tool prints a Markdown report and writes it, with the same data as JSON, to `calibration-<dataset>-<embedder>.md` and `.json` in the `--out` folder.

## 3. Read the report

The report opens with the suggested values and the evidence for each:

```text
## ollama:bge-m3 on my-texts

… items in … groups, replayed in a random order into clusters made from their labels. Suggested thresholds:

| Option | Value | Why |
|---|---|---|
| `JoinSimilarity` | … | At or above it, the nearest cluster was the item's own …% of the time; …% of the items whose group had a cluster joined it without a question |
| `CheckSimilarity` | … | …% of the items whose group had a cluster would have it put to a judge: among their nearest that a judge is asked about, and at least this close |
| `MergeSimilarity` | … | At or above it, a pair of parts was of one group …% of the time |
| `MergeCheckSimilarity` | … | …% of the groups cut in two had their parts among each other's ten nearest and at least this close |

By preference (pair it with the judge's `ClusteringPreference`; the check thresholds stay the same):

| Preference | `JoinSimilarity` | `CheckSimilarity` | `MergeSimilarity` | `MergeCheckSimilarity` |
|---|---|---|---|---|
| Precision | … | … | … | … |
| Balanced (suggested) | … | … | … | … |
| Recall | … | … | … | … |
```

Then two tables, by similarity band of 0.05: how often an item's nearest cluster was its own, and how often a pair of cluster parts belonged to one group. They show where similarity stops being enough for your data.

How the values are chosen:

- **Joins.** The items arrive one at a time, in a random order, into clusters built from the labels (what a perfect clusterer would build). Each is compared with every cluster so far before it joins its own, so it meets clusters of every size, and sometimes its group has no cluster yet, as early in a stream. `JoinSimilarity` is the lowest similarity at which the nearest cluster is the item's own at least 95% of the time. The rate is fitted as rising with similarity, so a band of mostly wrong candidates is not hidden by the many easy ones above it.
- **Checks.** A judge is asked about an item's nearest clusters only, up to `MaxChecksPerItem` (5, its default) of them. `CheckSimilarity` keeps all but 1% of the items a judge could help (their own cluster among those five nearest) within the ask zone.
- **Merges.** Each group is cut in two, a fragment of one to five items and the rest, and every pair of parts is compared. A fragment is what the clusterer leaves apart in practice: a few items that came early or were refused, rarely half a group. `MergeSimilarity` is the lowest similarity at which a pair is of one group 95% of the time; `MergeCheckSimilarity` keeps all but 1% of the true pairs that are among each other's ten nearest within reach.
- Join and merge thresholds are rounded up, check thresholds down, to two decimals (three when all candidates are closer than two can tell apart).

A note under the table flags trouble. "No similarity reached 95% right for joins" means the suggested join threshold is 1.00: with this model and data, no similarity is safe enough to join without a judge, so every close item goes to the judge (or, cosine only, every item starts its own cluster). Consider another embedding model, or rely on the judge.

### Preferences

The suggested values are balanced: joins and merges without a question are right 95% of the time. The report adds two more sets, with the same check thresholds:

| Set | Joins and merges without a question right | Pair it with |
|---|---|---|
| `Precision` | 98% of the time: higher join and merge thresholds, so more goes to the judge | `ChatClientJudgeOptions.Preference = ClusteringPreference.Precision` |
| Balanced (the suggested values) | 95% of the time | `ClusteringPreference.Balanced` (the default) |
| `Recall` | 90% of the time: lower join and merge thresholds, so more joins and merges happen without a question | `ClusteringPreference.Recall` |

The judge's preference adds a tie-break to its prompt (when unsure, keep apart or put together); the threshold set moves the same way for the decisions made without a judge. The thresholds never shift at run time: choose a set and keep it.

## 4. Apply the values

Copy the four values into your options, or read them from the report's JSON file, whose top-level properties are named after the options (the `Precision` and `Recall` properties hold the other sets, as objects with the same four properties):

```csharp
using System.Text.Json;
using Akinly;

using var report = JsonDocument.Parse(File.ReadAllText("docs/evaluation/calibration-my-texts-ollama_bge-m3.json"));
var suggested = report.RootElement;

var options = new ClusteringOptions
{
    EmbeddingModel = "bge-m3",
    JoinSimilarity = suggested.GetProperty("JoinSimilarity").GetDouble(),
    CheckSimilarity = suggested.GetProperty("CheckSimilarity").GetDouble(),
    MergeSimilarity = suggested.GetProperty("MergeSimilarity").GetDouble(),
    MergeCheckSimilarity = suggested.GetProperty("MergeCheckSimilarity").GetDouble(),
};
```

For another preference, read the four from `suggested.GetProperty("Precision")` or `suggested.GetProperty("Recall")` instead, and set the judge's `Preference` to match.

Or in `appsettings.json`, under `Akinly` (see [Binding from configuration](../configuration.md#binding-from-configuration)). For cosine-only clustering, use the suggested `JoinSimilarity` and `MergeSimilarity` in `ClusteringOptions.CosineOnly(model, join, merge)`.

## 5. Check the result

Numbers on pairs are not the goal; clusters a reader trusts are. Cluster a sample of real data, then read whole clusters: does the label, or the first member, honestly describe every member? Too many mixed clusters: raise the join (and merge) threshold. Too many clusters of one: lower them, or lower the check thresholds so the judge sees more.

To measure the whole pipeline on your file, `akinly-eval evaluate` splits it in two, calibrates on one half and clusters the other, cosine only and (with `--judge`) with a judge, in several input orders, and reports the adjusted Rand index, NMI, pairwise precision, recall and F1, and questions and tokens per item:

```bash
dotnet run -c Release --project tools/Akinly.Evaluation -- evaluate --dataset my-texts.jsonl --embedder ollama:bge-m3 --judge ollama:gemma4:12b
```

Judges are `provider:model` too: `ollama`, `openai` (`OPENAI_API_KEY`) or `anthropic` (`ANTHROPIC_API_KEY`, such as `--judge anthropic:<model>`). Several judges, separated by commas, give a row each. A judge in the cloud costs money for every question: try a small `--sample` first.

Run the tool without arguments for every option. Results for the public datasets are in [docs/evaluation](../evaluation/README.md).
