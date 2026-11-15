# Evaluation

How Akinly 1.0 clusters five public labeled datasets, against simple baselines run on the same embeddings and against published results, and how to rerun it.

Akinly works incrementally: texts arrive one at a time and are placed in one pass. Nobody tells it how many groups there are, and it never sees the labels. Every number here was measured that way.

## In short

- **The judge pays for itself.** Clustering by similarity alone, incrementally, reaches 29–52% accuracy on the intent datasets at the threshold that's best on the labels, and 9–45% at calibrated thresholds. With a judge, Akinly reaches 43–80%.
- **Akinly roughly matches k-means that is told the true number of groups, without being told it.** It's ahead on CLINC150 (both setups) and on Banking77 with Claude Haiku. It's level on MASSIVE with Haiku, and behind on Banking77 and MASSIVE with the local model.
- **Short-text streams.** On News-T, with a local 12B model, NMI is 0.876. That's above the published one-pass stream clusterer OSDM (0.858) and level with MStreamF (0.873), which passes over each batch ten times. Nothing was tuned on News-T. With OpenAI embeddings, two baselines beat it on both streams: k-means told k, and a threshold chosen on the labels (on Tweets-T, on accuracy and ARI, level on NMI; see [Short-text streams](#short-text-streams)). On Tweets-T (NMI 0.879 locally, 0.867 in the cloud), the batched MStreamF (0.923) and FastStream (0.901) are higher.
- **It's cheap.** Claude Haiku 5.5 as the judge costs $0.05–0.13 per 1,000 texts.
- **The main weakness is fragmentation.** Precision is high, recall is lower, and it makes more clusters than there are groups. Many fragments are small and sit next to their group's main cluster.
- **Published methods given more help score higher on the intent datasets.** For example, ClusterLLM is told the number of groups and fine-tunes its embedder per dataset. See [Published results](#published-results).

## Setup

**Datasets.** They're downloaded from a fixed version and checked against their SHA-256, never committed.

| Dataset | Texts measured | Groups | What | License |
|---|---|---|---|---|
| Banking77 | 3,080 (test split) | 77 | Customer-service requests to a bank (Casanueva et al., 2020) | CC BY 4.0 |
| CLINC150 | 4,500 (test split; out-of-scope left out) | 150 | Requests to a virtual assistant (Larson et al., 2019) | CC BY 3.0 |
| MASSIVE | 2,974 (English test split) | 59 | Requests to a virtual assistant (FitzGerald et al., 2022) | CC BY 4.0 |
| News-T | 11,109 (all, in stream order) | 152 | News titles, preprocessed: lowercase, stop words removed (Yin et al., 2018) | None stated; read for evaluation only, never redistributed |
| Tweets-T | 30,322 (all, in stream order) | 269 | Tweets, preprocessed the same way (Yin et al., 2018) | As News-T |

**Two setups**, both with the library's defaults: three clusters offered per join question, at most five refusals per item, and the default prompts.

| Setup | Embeddings | Judge |
|---|---|---|
| Local | `qwen3-embedding:8b` (Ollama) | `gemma4:12b` (Ollama, thinking off), on one RTX 5080 |
| Cloud | OpenAI `text-embedding-3-small` | Claude Haiku 5.5 (low effort) |

**Thresholds.** They're calibrated with the `calibrate` method on 3,000 texts. Those texts are never the ones measured:
- for the intent datasets, they come from the training split;
- for News-T and Tweets-T, which have no split, each is calibrated on the other.

**Order.** The intent datasets are shuffled once (seed 1). The stream datasets keep their file order. Each number is one run in one order, not an average over orders, so treat differences of a point or two as noise.

**Criterion.** The criterion is the phrase that tells the judge what "the same" means (see [The criterion](../concepts.md#the-criterion)). There are two configurations:
- **Default criterion**: "about the same specific problem or request, so that one answer or fix would settle both; sharing a topic is not enough". This is what you get without changing anything.
- **Criterion for the data**: what a user would write for this kind of text.
  - For the intent datasets, it's one of two candidates, picked on a development sample of the training split: the default (kept for Banking77) or "asking for the same kind of request: the same action or the same information, whatever the details such as names, dates, places or amounts; sharing a topic is not enough" (CLINC150 and MASSIVE).
  - For News-T and Tweets-T: "about the same news story or event; sharing a broad topic is not enough". It was decided up front, not tuned.

**Baselines**, run on the same embeddings and texts:
- **k-means, told k:** k-means with the true number of groups, all texts at once. It's a reference for what the embeddings support when the answer's size is known, not a competitor.
- **Similarity, best threshold:** cosine-only incremental clustering at the threshold that scores best on the labels. This is optimistic, since real use has no labels to tune on.
- **Similarity, calibrated:** cosine-only incremental clustering at the calibrated thresholds, with no judge.

**Metrics.** These are the standard clustering scores:
- **ACC**: accuracy under the best one-to-one matching of clusters to groups;
- **NMI**: normalized mutual information;
- **ARI**: adjusted Rand index;
- **P** and **R**: pairwise precision and recall;
- **Clusters**: the number of clusters made.

## Results

### Intent datasets (test splits)

ACC / NMI / ARI in percent.

| Setup | Method | Banking77 | CLINC150 | MASSIVE |
|---|---|---|---|---|
| Local | **Akinly, criterion for the data** | **57.8 / 82.7 / 51.5** | **76.2 / 92.3 / 74.3** | **50.7 / 77.0 / 40.5** |
| Local | Akinly, default criterion | 57.8 / 82.7 / 51.5 | 72.9 / 91.7 / 73.6 | 43.4 / 75.1 / 33.4 |
| Local | k-means, told k | 67.9 / 85.5 / 60.4 | 69.5 / 91.2 / 66.9 | 57.7 / 76.3 / 49.4 |
| Local | Similarity, best threshold | 50.1 / 82.0 / 47.1 | 52.4 / 86.1 / 55.0 | 34.5 / 69.9 / 28.6 |
| Local | Similarity, calibrated | 20.4 / 74.8 / 13.4 | 44.5 / 84.9 / 45.9 | 12.9 / 65.7 / 3.5 |
| Cloud | **Akinly, criterion for the data** | **63.6 / 84.4 / 57.1** | **80.3 / 93.7 / 78.8** | **54.3 / 78.0 / 45.7** |
| Cloud | Akinly, default criterion | 63.6 / 84.4 / 57.1 | 77.3 / 93.1 / 77.5 | 43.7 / 75.4 / 32.2 |
| Cloud | k-means, told k | 57.2 / 80.3 / 49.5 | 69.3 / 90.5 / 66.5 | 54.4 / 72.3 / 44.2 |
| Cloud | Similarity, best threshold | 37.3 / 76.8 / 30.7 | 45.2 / 84.0 / 46.3 | 28.9 / 67.1 / 23.4 |
| Cloud | Similarity, calibrated | 17.2 / 74.1 / 10.9 | 34.4 / 82.5 / 32.6 | 8.5 / 64.8 / 1.7 |

For Banking77, the criterion picked for the data is the default, so its two rows are the same run.

Akinly's clusters:

| Setup, criterion | Banking77 (77 groups) | CLINC150 (150) | MASSIVE (59) |
|---|---|---|---|
| Local, for the data | 334 clusters, P 0.56, R 0.49 | 427, P 0.81, R 0.69 | 531, P 0.80, R 0.28 |
| Cloud, for the data | 249, P 0.59, R 0.56 | 339, P 0.84, R 0.75 | 521, P 0.84, R 0.32 |

### Short-text streams

NMI / ACC / ARI in percent; one pass in the file's order.

| Setup | Method | News-T | Tweets-T |
|---|---|---|---|
| Local | **Akinly, news criterion** | **87.6 / 67.2 / 60.4** | **87.9 / 65.2 / 62.3** |
| Local | Akinly, default criterion | 86.4 / 63.3 / 55.2 | not run (time) |
| Local | k-means, told k | 86.4 / 66.3 / 60.1 | 88.6 / 64.8 / 54.9 |
| Local | Similarity, best threshold | 83.8 / 64.0 / 62.1 | 83.3 / 60.3 / 62.1 |
| Local | Similarity, calibrated | 82.9 / 54.2 / 49.2 | 82.0 / 46.8 / 43.3 |
| Cloud | **Akinly, news criterion** | **86.1 / 61.5 / 53.8** | **86.7 / 61.0 / 56.6** |
| Cloud | Akinly, default criterion | 85.2 / 58.6 / 52.1 | 85.7 / 58.0 / 54.5 |
| Cloud | k-means, told k | 87.8 / 67.4 / 63.2 | 89.6 / 66.3 / 58.0 |
| Cloud | Similarity, best threshold | 88.3 / 72.4 / 70.1 | 86.6 / 68.6 / 72.3 |
| Cloud | Similarity, calibrated | 81.9 / 50.8 / 43.0 | 81.9 / 47.1 / 45.9 |

Akinly's clusters with the news criterion: on News-T, 1,081 locally (P 0.94, R 0.45) and 1,439 in the cloud (P 0.94, R 0.38), for 152 groups; on Tweets-T, 2,659 locally (P 0.93, R 0.47) and 3,782 in the cloud (P 0.94, R 0.41), for 269 groups.

With OpenAI's embeddings, Akinly loses on both streams: k-means told k beats it, and so does a single threshold chosen on the stream's own labels (on Tweets-T on accuracy and ARI; NMI is level, 86.6 against 86.7). Those are the results here where the baselines win. The threshold needs labeled data from the stream itself, which a live stream doesn't have, and k-means needs the number of groups and the whole stream at once. With the local setup, Akinly leads on News-T, and on Tweets-T it leads on accuracy and ARI while k-means is ahead on NMI.

### Precision or recall

`ClusteringPreference` with the matching calibrated thresholds, in the cloud setup with the default criterion. Read the P and R columns: they show the preference doing what it says.

| Preference | Banking77: ACC, P, R, clusters | CLINC150 | MASSIVE |
|---|---|---|---|
| Precision | 63.5, 0.63, 0.53, 289 | 70.2, 0.94, 0.59, 650 | 37.7, 0.89, 0.15, 1,007 |
| Balanced (default) | 63.6, 0.59, 0.56, 249 | 77.3, 0.88, 0.70, 424 | 43.7, 0.86, 0.20, 755 |
| Recall | 58.5, 0.45, 0.60, 173 | 76.3, 0.74, 0.75, 305 | 52.8, 0.79, 0.30, 452 |

### The library's default thresholds

The same runs with the library's default thresholds instead of a calibration per dataset: `new ClusteringOptions { EmbeddingModel = "text-embedding-3-small" }`. Cloud setup, default criterion. The defaults were calibrated on texts pooled from the training splits of these three datasets, so this is a check within the same domain, not a cold start.

| | Banking77 | CLINC150 | MASSIVE |
|---|---|---|---|
| Default thresholds, ACC / NMI | 59.3 / 82.7 | 76.0 / 93.1 | 44.5 / 75.3 |
| Calibrated on the training split | 63.6 / 84.4 | 77.3 / 93.1 | 43.7 / 75.4 |

The defaults come within about 4 points of thresholds calibrated on each dataset's own training split.

### Cost and time

| | Judge input per text | Judge cost per 1,000 texts | Time per 1,000 texts |
|---|---|---|---|
| Cloud, Claude Haiku 5.5 | 480–1,210 tokens | $0.05–0.13 | 2–4 minutes (8 requests at once) |
| Local, gemma4:12b on one RTX 5080 | 340–760 tokens | none | 3–6 minutes |

Haiku 5.5 is priced at $0.10 per million input tokens and $0.50 per million output tokens. Answers are a few tokens, plus little thinking at low effort. Every question goes out live; a provider's batch API would halve the price.

## Published results

These are for context only. None of them was run in this repository, and the protocols differ: some are told the number of groups, some fine-tune models per dataset, some pass over the data many times. Test split sizes also differ between papers.

**Not told the number of groups:**

| Method | Setting | Result |
|---|---|---|
| LLM-MemCluster ([2025](https://arxiv.org/abs/2511.15424)) | Single pass, GPT-4.1-mini, given a range for k | MASSIVE: ACC 54.8, NMI 73.5, ARI 47.9 |
| IntentGPT ([2024](https://arxiv.org/abs/2411.10670)) | GPT-4, starts from part of the known intents | Banking77: ACC 64.2, NMI 81.4, ARI 55.2; CLINC150: ACC 83.2, NMI 94.4, ARI 78.7 |
| OSDM ([ACL 2020](https://aclanthology.org/2020.acl-main.70.pdf)) | One pass, a statistical model | News-T: NMI 85.8 |
| MStreamF ([KDD 2018](https://github.com/jackyin12/MStream)) | Batches of the stream, 10 passes over each | News-T: NMI 87.3; Tweets-T: NMI 92.3 |
| FastStream ([2021](https://arxiv.org/abs/2101.08595)) | Shuffled streams, 20 runs, settings tuned on these datasets | Best on News-T: NMI 88.3; on Tweets-T: NMI 90.1 |

**Told the number of groups:**

| Method | Setting | Result |
|---|---|---|
| ClusterLLM ([EMNLP 2023](https://arxiv.org/abs/2305.14871)) | Fine-tunes the embedder per dataset from GPT-3.5's answers | Banking77: ACC 71.2, NMI 85.2; CLINC150: ACC 83.8, NMI 94.0 |

Compared in its own setting, Akinly is level with LLM-MemCluster on MASSIVE (cloud: ACC 54.3 against 54.8, NMI 78.0 against 73.5). On News-T, the local setup is above the one-pass OSDM and level with the batched MStreamF; the cloud setup is a little below both, and FastStream, tuned on these datasets, is higher. On Tweets-T, both batched methods are clearly higher (MStreamF 92.3, FastStream 90.1, against Akinly's 87.9 and 86.7); no one-pass result on Tweets-T was found to compare with. It's behind methods that are told the number of groups or tune per dataset.

## What the numbers say

- **Where Akinly fits.** Text arrives over time, nobody knows how many groups there will be, and clusters must keep their ids. That's what Akinly is built for, and in that setting it gets roughly what an offline method gets when it's handed the answer's size.
- **The judge does the work.** Similarity alone, incrementally, can't find the groups: with calibrated thresholds it makes thousands of clusters. The judge brings incremental clustering up to k-means told k.
- **Fit the criterion to your data.** It's the strongest control over how fine the clusters are: on MASSIVE it moved accuracy by 7–11 points.
- **Expect more clusters than groups.** Akinly leans toward precision: it would rather leave two clusters of one group apart than mix two groups. On MASSIVE, recall stays low (0.28–0.32). `ClusteringPreference.Recall` with its thresholds trades some precision for fewer clusters.
- **A better judge helps, and costs little.** The cloud setup (OpenAI embeddings, Claude Haiku) beats the local one (`qwen3-embedding:8b`, `gemma4:12b`) on every intent dataset, for about ten cents per thousand texts. The two setups differ in embeddings too, so this compares setups, not judges alone.

## Rerun it

From the repository root, with `OPENAI_API_KEY` and `ANTHROPIC_API_KEY` set for the cloud setup, or Ollama running for the local one. Each command prints its scores.

```bash
# Intent datasets (banking77, clinc150, massive), whole test split, calibrated on the training split
dotnet run -c Release --project tools/Akinly.Evaluation -- experiment --dataset clinc150 --split test --dev 0 \
  --embedder openai:text-embedding-3-small --judge anthropic:claude-haiku-5-5 --parallel 8 \
  --criterion "asking for the same kind of request: the same action or the same information, whatever the details such as names, dates, places or amounts; sharing a topic is not enough"

# The baselines: --judge none
# A preference: --prefer precision or --prefer recall; the library's defaults: --thresholds defaults

# Streams, in file order, each calibrated on the other
dotnet run -c Release --project tools/Akinly.Evaluation -- experiment --dataset news-t --dev 0 --order file --calibrate-on tweets-t \
  --embedder ollama:qwen3-embedding:8b --judge ollama:gemma4:12b \
  --criterion "about the same news story or event; sharing a broad topic is not enough"
```

`experiment` also diagnoses its run. It counts the judge's answers against the labels, and says why each fragment of a group stayed apart from the group's main cluster (a refusal, a "different" answer, or below the merge-check threshold).

## How the design was tuned

Every choice was made on development samples of the training splits, never on the texts measured above:
- three clusters offered per join question;
- the wording of the choice and merge prompts;
- the calibration of merges on small fragments;
- the two candidate criteria.

With a perfect judge (the labels answering), the same structure reached about 94% accuracy on Banking77 and CLINC150 development samples. The remaining gap is the judge's.
