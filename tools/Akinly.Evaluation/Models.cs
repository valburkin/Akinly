using System.ClientModel;
using Anthropic;
using Microsoft.Extensions.AI;
using OllamaSharp;
using OllamaSharp.Models.Chat;
using OpenAI;

namespace Akinly.Evaluation;

/// <summary>
/// Models named as <c>provider:model</c>: <c>ollama:bge-m3</c> (a local Ollama at OLLAMA_HOST,
/// default http://localhost:11434), <c>openai:text-embedding-3-small</c> (OPENAI_API_KEY), or
/// <c>anthropic:claude-haiku-5-5</c> (ANTHROPIC_API_KEY; chat models only).
/// </summary>
public static class Models
{
    public static IEmbeddingGenerator<string, Embedding<float>> Embedder(string spec)
    {
        var (provider, model) = Split(spec);
        return provider switch
        {
            "ollama" => new OllamaApiClient(OllamaHost(), model),
            "openai" => new OpenAIClient(new ApiKeyCredential(OpenAIKey())).GetEmbeddingClient(model).AsIEmbeddingGenerator(),
            "anthropic" => throw new ArgumentException("Anthropic has no embedding models: embed with ollama or openai."),
            _ => throw Unknown(provider),
        };
    }

    public static IChatClient Chat(string spec)
    {
        var (provider, model) = Split(spec);
        return provider switch
        {
            "ollama" => new OllamaApiClient(OllamaHost(), model),
            "openai" => new OpenAIClient(new ApiKeyCredential(OpenAIKey())).GetChatClient(model).AsIChatClient(),
            "anthropic" => new AnthropicClient { ApiKey = AnthropicKey() }.AsIChatClient(model, defaultMaxOutputTokens: 4096),
            _ => throw Unknown(provider),
        };
    }

    /// <summary>
    /// Request options for a chat model: the model id, and the least reasoning the provider allows
    /// (a yes/no judgment does not need more): thinking off for Ollama, low effort for Anthropic.
    /// </summary>
    public static ChatOptions ChatOptions(string spec)
    {
        var (provider, model) = Split(spec);
        var options = new ChatOptions { ModelId = model };
        if (provider == "ollama")
        {
            options.RawRepresentationFactory = _ => new ChatRequest { Think = false };
        }
        else if (provider == "anthropic")
        {
            options.Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Low };
        }

        return options;
    }

    /// <summary>Whether a judge on this model should ask for a temperature of 0: local models take one; current cloud models reject it.</summary>
    public static bool AcceptsTemperature(string spec) => Split(spec).Provider == "ollama";

    public static string ModelName(string spec) => Split(spec).Model;

    /// <summary>Where a model is served from, so the same name on two servers is not mistaken for one model.</summary>
    public static string Endpoint(string spec) => Split(spec).Provider switch
    {
        "ollama" => OllamaHost().ToString(),
        "openai" => "https://api.openai.com/",
        "anthropic" => "https://api.anthropic.com/",
        var provider => throw Unknown(provider),
    };

    /// <summary>Checks a spec without connecting to anything.</summary>
    public static void Check(string spec)
    {
        var (provider, _) = Split(spec);
        if (provider is not ("ollama" or "openai" or "anthropic"))
        {
            throw Unknown(provider);
        }
    }

    private static (string Provider, string Model) Split(string spec)
    {
        var colon = spec.IndexOf(':', StringComparison.Ordinal);
        return colon <= 0 || colon == spec.Length - 1
            ? throw new ArgumentException($"'{spec}' is not provider:model, such as ollama:bge-m3.")
            : (spec[..colon], spec[(colon + 1)..]);
    }

    /// <summary>OLLAMA_HOST as Ollama itself reads it: a scheme is optional.</summary>
    internal static Uri OllamaHost()
    {
        var host = Environment.GetEnvironmentVariable("OLLAMA_HOST");
        if (string.IsNullOrWhiteSpace(host))
        {
            return new Uri("http://localhost:11434");
        }

        host = host.Contains("://", StringComparison.Ordinal) ? host : "http://" + host;
        return new UriBuilder(host) { Host = new Uri(host).Host == "0.0.0.0" ? "localhost" : new Uri(host).Host }.Uri;
    }

    private static string OpenAIKey() =>
        Environment.GetEnvironmentVariable("OPENAI_API_KEY") is { Length: > 0 } key
            ? key
            : throw new ArgumentException("Set OPENAI_API_KEY to use OpenAI models.");

    private static string AnthropicKey() =>
        Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") is { Length: > 0 } key
            ? key
            : throw new ArgumentException("Set ANTHROPIC_API_KEY to use Anthropic models.");

    private static ArgumentException Unknown(string provider) => new($"Unknown provider '{provider}': use ollama, openai or anthropic.");
}
