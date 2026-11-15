# Akinly.AI

[Akinly](https://www.nuget.org/packages/Akinly) with any language model through Microsoft.Extensions.AI: texts in, clusters out. Works with local models (Ollama) and cloud ones (OpenAI, Azure OpenAI, and any provider with an `IChatClient` or `IEmbeddingGenerator` adapter).

- **`TextClusterer`**: embeds texts with any `IEmbeddingGenerator<string, Embedding<float>>`, places them, has a chat model decide the close calls, merges clusters that turn out to be one, and labels them, in one call.
- **`ChatClientJudge`**: decides close calls ("does this item belong in this cluster?", "are these two clusters one?") with any `IChatClient`, answering `{"same": true}` or `{"same": false}`, or, for a join question that offers several nearby clusters, `{"group": n}` (0 for none). `Preference` tells it which mistake to rather make on a close call (precision or recall). Texts are fenced as JSON data, requests carry no tools, and every failure becomes a retry, never a "no".
- **`ChatClientLabeler`**: titles clusters once they reach a size, and again as they grow or shrink.
- **`JudgePrompts`**: the judge's prompts, request options and answer parsing, to send questions through a provider's batch API instead of live.
- Dependency injection: `UseChatClientJudge`, `AddChatClientLabeler`, `AddTextClusterer`.

## Install

```bash
dotnet add package Akinly.AI
dotnet add package OllamaSharp   # or the adapter for your provider, such as Microsoft.Extensions.AI.OpenAI
```

Requires .NET 10.

## Example

With a local [Ollama](https://ollama.com), two models pulled (`ollama pull qwen3-embedding:8b`, `ollama pull gemma4:12b`), and the OllamaSharp package added:

```csharp
using Akinly;
using Akinly.AI;
using Microsoft.Extensions.AI;
using OllamaSharp;
using OllamaSharp.Models.Chat;

var ollama = new Uri("http://localhost:11434");
using var embedder = new OllamaApiClient(ollama, "qwen3-embedding:8b");   // embeds the texts
using var chat = new OllamaApiClient(ollama, "gemma4:12b");               // decides close calls and writes labels

// Thresholds calibrated for qwen3-embedding:8b; the defaults are for OpenAI's text-embedding-3-small.
var options = new ClusteringOptions
{
    EmbeddingModel = "qwen3-embedding:8b",
    JoinSimilarity = 0.96,
    CheckSimilarity = 0.55,
    MergeSimilarity = 0.97,
    MergeCheckSimilarity = 0.71,
};

// A short answer needs no reasoning: with thinking off, the model answers much faster.
var noThinking = new ChatOptions { RawRepresentationFactory = _ => new ChatRequest { Think = false } };
var textOptions = new TextClustererOptions { Judge = { ChatOptions = noThinking }, Labeler = { ChatOptions = noThinking } };

var clusterer = new TextClusterer(embedder, chat, options, textOptions);

await clusterer.AddAsync(
[
    new TextItem("t1", "The heating in my apartment has been broken for two weeks."),
    new TextItem("t2", "There is no heat in our unit and it is freezing."),
    new TextItem("t3", "My landlord raised the rent by 30 percent this year."),
]);

await foreach (var cluster in clusterer.ListClustersAsync())
{
    var members = await clusterer.GetMembersAsync(cluster.Id);
    Console.WriteLine($"{cluster.Id} {cluster.Label ?? "(no label yet)"}: {string.Join(", ", members)}");
}
```

For cosine-only clustering without a chat model, pass `chat: null` and `ClusteringOptions.CosineOnly(...)`.

## Documentation

- [Use a local Ollama model](https://github.com/valburkin/Akinly/blob/main/docs/how-to/use-ollama.md)
- [Use OpenAI, Azure OpenAI or any other provider](https://github.com/valburkin/Akinly/blob/main/docs/how-to/use-openai.md)
- [Answer questions through a batch API](https://github.com/valburkin/Akinly/blob/main/docs/how-to/answer-questions-yourself.md#through-a-providers-batch-api)
- [Label clusters](https://github.com/valburkin/Akinly/blob/main/docs/how-to/label-clusters.md)
- [Calibrate thresholds for your embedding model](https://github.com/valburkin/Akinly/blob/main/docs/how-to/calibrate-thresholds.md)
- [Run in ASP.NET Core or a worker service](https://github.com/valburkin/Akinly/blob/main/docs/how-to/dependency-injection.md)
- [All guides](https://github.com/valburkin/Akinly/blob/main/docs/README.md) and the [changelog](https://github.com/valburkin/Akinly/blob/main/CHANGELOG.md)

Akinly is open source under the [MIT license](https://github.com/valburkin/Akinly/blob/main/LICENSE), on [GitHub](https://github.com/valburkin/Akinly). It was written with Claude Code, Anthropic's AI coding assistant, under the maintainer's direction; [how it was built](https://github.com/valburkin/Akinly#how-this-was-built).
