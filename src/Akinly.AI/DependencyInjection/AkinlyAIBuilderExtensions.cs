using Akinly;
using Akinly.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers Akinly's AI adapters, over the container's <see cref="IChatClient"/> and embedding
/// generator. Each call replaces what an earlier call of the same kind registered; options are
/// checked when the call is made, so a bad setting fails at startup.
/// </summary>
public static class AkinlyAIBuilderExtensions
{
    /// <summary>
    /// Uses a <see cref="ChatClientJudge"/> over the container's <see cref="IChatClient"/> (or the
    /// one registered under <paramref name="chatClientKey"/>), reading texts from the
    /// container's <see cref="IItemTextStore"/> (an <see cref="InMemoryItemTextStore"/> unless you
    /// registered one).
    /// </summary>
    /// <param name="builder">The Akinly builder.</param>
    /// <param name="configure">Sets the judge's options.</param>
    /// <param name="chatClientKey">The key of a keyed <see cref="IChatClient"/>, to use a different model for the judge; null for the default one.</param>
    /// <exception cref="ArgumentException">The judge's options are invalid.</exception>
    public static AkinlyBuilder UseChatClientJudge(this AkinlyBuilder builder, Action<ChatClientJudgeOptions>? configure = null, object? chatClientKey = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = new ChatClientJudgeOptions();
        configure?.Invoke(options);
        Check(options.Validate(), "judge");
        AddTexts(builder.Services);
        builder.Services.RemoveAll<IClusterJudge>();
        builder.Services.AddSingleton<IClusterJudge>(sp => new ChatClientJudge(
            Chat(sp, chatClientKey),
            sp.GetRequiredService<IItemTextStore>(),
            options,
            sp.GetService<ILogger<ChatClientJudge>>()));
        return builder;
    }

    /// <summary>Registers a <see cref="ChatClientLabeler"/> over the container's <see cref="IChatClient"/> (or a keyed one).</summary>
    /// <param name="builder">The Akinly builder.</param>
    /// <param name="configure">Sets the labeler's options.</param>
    /// <param name="chatClientKey">The key of a keyed <see cref="IChatClient"/>, to use a different model for labels; null for the default one.</param>
    /// <exception cref="ArgumentException">The labeler's options are invalid.</exception>
    public static AkinlyBuilder AddChatClientLabeler(this AkinlyBuilder builder, Action<ChatClientLabelerOptions>? configure = null, object? chatClientKey = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = new ChatClientLabelerOptions();
        configure?.Invoke(options);
        Check(options.Validate(), "labeler");
        AddTexts(builder.Services);
        builder.Services.RemoveAll<ChatClientLabeler>();
        builder.Services.AddSingleton(sp => new ChatClientLabeler(
            Chat(sp, chatClientKey),
            sp.GetRequiredService<IItemTextStore>(),
            options,
            sp.GetService<ILogger<ChatClientLabeler>>()));
        return builder;
    }

    /// <summary>
    /// Registers a <see cref="TextClusterer"/> over the container's clusterer, embedding generator,
    /// text store, judge (if any) and labeler (if any).
    /// </summary>
    /// <param name="builder">The Akinly builder.</param>
    /// <param name="configure">Sets the text clusterer's options (its judge and labeler options are not used here).</param>
    public static AkinlyBuilder AddTextClusterer(this AkinlyBuilder builder, Action<TextClustererOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = new TextClustererOptions();
        configure?.Invoke(options);
        AddTexts(builder.Services);
        builder.Services.RemoveAll<TextClusterer>();
        builder.Services.AddSingleton(sp => new TextClusterer(
            sp.GetRequiredService<Clusterer>(),
            sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(),
            sp.GetRequiredService<IItemTextStore>(),
            sp.GetService<IClusterJudge>(),
            sp.GetService<ChatClientLabeler>(),
            options));
        return builder;
    }

    /// <summary>
    /// The texts are kept in one store that the judge, the labeler and the text clusterer all
    /// use: an <see cref="InMemoryItemTextStore"/> unless an <see cref="IItemTextStore"/> is registered.
    /// </summary>
    private static void AddTexts(IServiceCollection services)
    {
        if (services.Any(d => d.ServiceType == typeof(IItemTextSource)) && !services.Any(d => d.ServiceType == typeof(IItemTextStore)))
        {
            throw new InvalidOperationException(
                "An IItemTextSource is registered without an IItemTextStore: Akinly's adapters read and write texts through one IItemTextStore. Register yours as IItemTextStore.");
        }

        services.TryAddSingleton<IItemTextStore, InMemoryItemTextStore>();
        services.TryAddSingleton<IItemTextSource>(sp => sp.GetRequiredService<IItemTextStore>());
    }

    private static IChatClient Chat(IServiceProvider services, object? key) =>
        key is null ? services.GetRequiredService<IChatClient>() : services.GetRequiredKeyedService<IChatClient>(key);

    private static void Check(IReadOnlyList<string> errors, string what)
    {
        if (errors.Count > 0)
        {
            throw new ArgumentException($"Invalid {what} options: " + string.Join(" ", errors));
        }
    }
}
