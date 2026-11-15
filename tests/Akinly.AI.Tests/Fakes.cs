using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Akinly.AI.Tests;

/// <summary>A chat client that answers with a function and records every request.</summary>
internal sealed class FakeChatClient(Func<IReadOnlyList<ChatMessage>, ChatOptions?, string> answer) : IChatClient
{
    private int _inFlight;

    public ConcurrentQueue<(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options)> Requests { get; } = new();

    public int MaxInFlight { get; private set; }

    public TimeSpan Delay { get; init; }

    public Exception? Throw { get; init; }

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        Requests.Enqueue((list, options));
        var now = Interlocked.Increment(ref _inFlight);
        lock (Requests)
        {
            MaxInFlight = Math.Max(MaxInFlight, now);
        }

        try
        {
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (Throw is not null)
            {
                throw Throw;
            }

            return new ChatResponse(new ChatMessage(ChatRole.Assistant, answer(list, options)));
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

/// <summary>
/// An embedding model for tests: each text points along the axis of the first keyword it
/// contains, tilted a little by its length, so texts sharing a keyword are close.
/// </summary>
internal sealed class FakeEmbedder(params string[] keywords) : IEmbeddingGenerator<string, Embedding<float>>
{
    public const string ModelId = "fake-embedding";

    public List<int> BatchSizes { get; } = [];

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var texts = values.ToList();
        BatchSizes.Add(texts.Count);
        var embeddings = texts.Select(text =>
        {
            var vector = new float[keywords.Length + 1];
            var axis = Array.FindIndex(keywords, k => text.Contains(k, StringComparison.OrdinalIgnoreCase));
            vector[axis < 0 ? keywords.Length : axis] = 1;
            vector[(axis + 1 + keywords.Length) % vector.Length] += (text.Length % 7) * 0.02f;
            return new Embedding<float>(vector);
        });
        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(embeddings));
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(EmbeddingGeneratorMetadata) ? new EmbeddingGeneratorMetadata("fake", null, ModelId) : null;

    public void Dispose()
    {
    }
}
