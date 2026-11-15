# Use OpenAI, Azure OpenAI or any other provider

`Akinly.AI` talks to models only through two Microsoft.Extensions.AI interfaces: `IEmbeddingGenerator<string, Embedding<float>>` for embeddings and `IChatClient` for the judge and the labeler. Any provider with an adapter for them works, and the embedding model and the chat model can come from different providers.

## OpenAI

Add the adapter package, which brings the OpenAI SDK:

```bash
dotnet add package Microsoft.Extensions.AI.OpenAI
```

```csharp
using Akinly;
using Akinly.AI;
using Microsoft.Extensions.AI;
using OpenAI;

var openai = new OpenAIClient(Environment.GetEnvironmentVariable("OPENAI_API_KEY")
    ?? throw new InvalidOperationException("Set OPENAI_API_KEY."));

var chatModel = "your-chat-model";   // any chat model your account can use; a small, fast one is enough for short answers

using IEmbeddingGenerator<string, Embedding<float>> embedder =
    openai.GetEmbeddingClient("text-embedding-3-small").AsIEmbeddingGenerator();
using IChatClient chat = openai.GetChatClient(chatModel).AsIChatClient();

// The default thresholds were calibrated for text-embedding-3-small at its default 1,536 dimensions.
var clusterer = new TextClusterer(embedder, chat, new ClusteringOptions { EmbeddingModel = "text-embedding-3-small" });

await clusterer.AddAsync(
[
    new TextItem("t1", "I was charged twice for the same order."),
    new TextItem("t2", "My card shows a duplicate payment for one purchase."),
    new TextItem("t3", "How do I change the delivery address?"),
]);
```

`AsIEmbeddingGenerator(defaultModelDimensions: 1024)` asks the embedding model for shorter vectors, which take less space in a vector database. The default thresholds were not calibrated at other lengths: if you shorten the vectors, [calibrate](calibrate-thresholds.md) for that length. Whatever you choose, keep it: a store holds one length, and changing it means a new store.

Never put an API key in source code; read it from the environment, user secrets, or a secret store.

## Azure OpenAI

Azure OpenAI serves the same API under your resource's address. The OpenAI SDK reaches it through its `Endpoint` option and your resource's key; the model name is your **deployment** name:

```csharp
using System.ClientModel;
using OpenAI;

var azure = new OpenAIClient(
    new ApiKeyCredential(Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY")
        ?? throw new InvalidOperationException("Set AZURE_OPENAI_API_KEY.")),
    new OpenAIClientOptions { Endpoint = new Uri("https://your-resource.openai.azure.com/openai/v1/") });

using IEmbeddingGenerator<string, Embedding<float>> embedder =
    azure.GetEmbeddingClient("your-embedding-deployment").AsIEmbeddingGenerator();
using IChatClient chat = azure.GetChatClient("your-chat-deployment").AsIChatClient();
```

The `Azure.AI.OpenAI` package's `AzureOpenAIClient` offers the same `GetChatClient` and `GetEmbeddingClient`, with Microsoft Entra ID sign-in; either works with Akinly. Set `EmbeddingModel` to the model behind the embedding deployment (such as `text-embedding-3-small`) rather than the deployment's name, so the store records which model's thresholds apply.

## Anthropic (Claude)

Claude has no embedding model, so pair it with another provider's embeddings. The evaluation's cloud setup is OpenAI's `text-embedding-3-small` with Claude Haiku as the judge. Add Anthropic's C# SDK, which brings its own `IChatClient`:

```bash
dotnet add package Anthropic
```

```csharp
using Anthropic;

using IChatClient claude = new AnthropicClient
    {
        ApiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
            ?? throw new InvalidOperationException("Set ANTHROPIC_API_KEY."),
    }
    .AsIChatClient("claude-haiku-5-5", defaultMaxOutputTokens: 4096);

// A short judgment needs little reasoning: low effort is faster and cheaper.
var lowEffort = new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Low } };
var textOptions = new TextClustererOptions
{
    Judge = { ChatOptions = lowEffort },
    Labeler = { ChatOptions = lowEffort },
};

var clusterer = new TextClusterer(embedder, claude, new ClusteringOptions { EmbeddingModel = "text-embedding-3-small" }, textOptions);
```

Here `embedder` is the OpenAI embedding generator from the first example. Current Claude models reject a temperature, so leave `UseZeroTemperature` off. To answer questions through Anthropic's Message Batches API at half the price, see [Answer questions yourself](answer-questions-yourself.md).

## Any other provider

Look for a package that gives you an `IChatClient` (and an `IEmbeddingGenerator` if the provider has embeddings). Then:

```csharp
IEmbeddingGenerator<string, Embedding<float>> embedder = /* any provider's embedding generator */;
IChatClient chat = /* any provider's chat client */;

var clusterer = new TextClusterer(embedder, chat, new ClusteringOptions { EmbeddingModel = "the-embedding-model" });
```

If you build the judge yourself, it takes the chat client and a text source:

```csharp
var texts = new InMemoryItemTextStore();   // or your own IItemTextSource over a database
var judge = new ChatClientJudge(chat, texts, new ChatClientJudgeOptions
{
    Criterion = "describing the same bug",
    ChatOptions = new ChatOptions { ModelId = "a-model-name" },
});
```

## Settings that differ between providers

| Situation | Setting |
|---|---|
| The provider rejects response schemas (structured output) | `UseStructuredOutput = false` on `ChatClientJudgeOptions` and `ChatClientLabelerOptions`. The judge still reads `{"same": true}` (or `{"group": 2}` for a question with choices), a bare `yes` or `no`, or the last such object in the text |
| The model accepts a temperature, and you want steadier answers | `UseZeroTemperature = true`. It is off by default, because current Claude models and OpenAI's reasoning models reject any temperature |
| One client serves several models | `ChatOptions = new ChatOptions { ModelId = "..." }` |
| Rate limits | Lower `MaxParallelRequests` (default 4); raise `FailedAnswers.RetryDelay` (default 1 second; `SettleAsync` waits it after any round with a failed answer, and doubles it, up to 5 minutes, while every answer fails) |
| A cheaper model for labels than for the judge | Build `ChatClientJudge` and `ChatClientLabeler` with different chat clients, or with dependency injection pass `chatClientKey` to `UseChatClientJudge` and `AddChatClientLabeler` ([example](dependency-injection.md#a-different-model-for-each-role)) |
| Many questions and no need for live answers | Send them through the provider's batch API: [Answer questions yourself or through a batch API](answer-questions-yourself.md#through-a-providers-batch-api) |

The judge's requests carry no tools and no conversation state, so each question stands alone. Middleware you add to the chat client (caching, logging, rate limiting through Microsoft.Extensions.AI's `ChatClientBuilder`) still sees every request.

## Thresholds

The defaults were calibrated for `text-embedding-3-small` at its default dimensions. For any other embedding model, including `text-embedding-3-large`, [calibrate them](calibrate-thresholds.md); the evaluation tool takes `--embedder openai:<model>` with `OPENAI_API_KEY` set.
