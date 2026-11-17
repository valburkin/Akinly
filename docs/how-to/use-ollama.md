# Use a local Ollama model

Run the whole pipeline on your own machine: embeddings and judge from [Ollama](https://ollama.com), no text leaves the computer.

## Before you start

1. Install Ollama and start it (`ollama serve`, or the desktop app). It listens on `http://localhost:11434`.
2. Pull an embedding model and a chat model. These two are the [evaluation](../evaluation/README.md)'s local setup (about 13 GB; a GPU with 16 GB runs both):

   ```bash
   ollama pull qwen3-embedding:8b
   ollama pull gemma4:12b
   ```

   On a smaller machine, `bge-m3` (1.2 GB) embeds well too; [Calibrate thresholds](calibrate-thresholds.md#calibrated-values) has values for both.

3. Reference `Akinly.AI` (see [Install](../getting-started.md#install)) and add the OllamaSharp package, whose `OllamaApiClient` is both an `IEmbeddingGenerator<string, Embedding<float>>` and an `IChatClient`:

   ```bash
   dotnet add package OllamaSharp
   ```

## The program

```csharp
using Akinly;
using Akinly.AI;
using Microsoft.Extensions.AI;
using OllamaSharp;
using OllamaSharp.Models.Chat;

var ollama = new Uri("http://localhost:11434");
using var embedder = new OllamaApiClient(ollama, "qwen3-embedding:8b");
using var chat = new OllamaApiClient(ollama, "gemma4:12b");

// Calibrated for qwen3-embedding:8b. The defaults (join 0.83, check 0.30, merge 0.72,
// merge check 0.55) are for OpenAI's text-embedding-3-small.
var options = new ClusteringOptions
{
    EmbeddingModel = "qwen3-embedding:8b",
    JoinSimilarity = 0.96,
    CheckSimilarity = 0.57,
    MergeSimilarity = 1.0,   // no similarity was safe without a judge
    MergeCheckSimilarity = 0.75,
};

// A short answer needs no long reasoning: ask the model not to think, which is faster.
var noThinking = new ChatOptions { RawRepresentationFactory = _ => new ChatRequest { Think = false } };
var textOptions = new TextClustererOptions
{
    Judge = { ChatOptions = noThinking, MaxParallelRequests = 2 },
    Labeler = { ChatOptions = noThinking },
};

var clusterer = new TextClusterer(embedder, chat, options, textOptions);

var step = await clusterer.AddAsync(
[
    new TextItem("heat-1", "The heating in my apartment has been broken for two weeks."),
    new TextItem("rent-1", "My landlord raised the rent by 30 percent this year."),
    new TextItem("wifi-1", "The building's wifi drops every evening."),
    new TextItem("heat-2", "There is no heat in our unit and it is freezing."),
    new TextItem("rent-2", "I can't afford the new rent after the increase."),
    new TextItem("heat-3", "Our radiators are cold and the landlord won't fix them."),
]);

Console.WriteLine($"{step.Placements.Count} placed, {step.Questions.Count} questions asked, {step.Merges.Count} merges");

await foreach (var cluster in clusterer.ListClustersAsync())
{
    var members = await clusterer.GetMembersAsync(cluster.Id);
    Console.WriteLine($"{cluster.Id} {cluster.Label ?? "(no label)"}: {string.Join(", ", members)}");
}
```

`AddAsync` embeds the texts in batches of 64, places them, puts each close call to the chat model and applies the answers until no question is left, runs a merge check, and asks the model for a label for each cluster that reached three members. The texts stay in memory in `clusterer.Texts`, so the judge and the labeler can quote them.

## Choose thresholds for your embedding model

Thresholds are cosine similarities, and every embedding model spreads similarities differently, so the defaults do not carry over to `bge-m3` or any other model. Calibrate them on labeled texts like yours, or on a public dataset, with the evaluation tool:

```bash
dotnet run -c Release --project tools/Akinly.Evaluation -- calibrate --dataset my-texts.jsonl --embedder ollama:bge-m3
```

[Calibrate thresholds](calibrate-thresholds.md) explains the input and the output.

## Tuning for a local model

- **Parallel requests.** The judge sends `MaxParallelRequests` (default 4) questions at once. Ollama serves several requests at a time only as far as its own settings and your memory allow, and queues the rest; requests that wait too long count as failed. On one consumer GPU, 1 or 2 is a good start.
- **Timeouts.** A request that takes longer than `RequestTimeout` (default 2 minutes) counts as a failed answer and is asked again, up to `FailedAnswers.MaxAttempts` (3) times. Raise it if a large model on a CPU is slow, rather than letting questions fail.
- **Thinking models.** The judge and the labeler read the answer after a model's `<think>`, `<thinking>` or `<reasoning>` block in the response text, so they work with thinking on, only slower. A short answer needs no reasoning: turn thinking off, as above.
- **Temperature.** Most local models accept a temperature: set `UseZeroTemperature = true` on the judge (and the labeler) for steadier answers. It is off by default because some cloud models reject any temperature.
- **Structured output.** The judge asks for `{"same": true}` (or `{"group": n}` for a question with choices) through a JSON schema. If your model or Ollama version rejects schemas, set `UseStructuredOutput = false` on the judge and the labeler; the answer is read either way.
- **Another machine.** Point the `Uri` at it, such as `http://gpu-box:11434`, and make Ollama listen there (`OLLAMA_HOST=0.0.0.0`). Ollama has no authentication: anyone who can reach that port can use your models, so allow it only on a trusted network or behind a firewall.
- **Dimensions.** A vector database needs the embedding length up front (`VectorDataClusterStoreOptions.Dimensions`): `qwen3-embedding:8b` returns 4,096 and `bge-m3` 1,024. Embed one text and check `Vector.Length` to be sure for any model.

## Without a chat model

Pass `chat: null` and cosine-only options to cluster with the embedding model alone: see [Cluster without a language model](cluster-without-a-model.md).
