using Akinly.Storage;
using Akinly.VectorData;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.VectorData;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers Akinly's vector database store.</summary>
public static class AkinlyVectorDataBuilderExtensions
{
    /// <summary>
    /// Keeps the clusters in a <see cref="VectorDataClusterStore"/> over the container's
    /// <see cref="VectorStore"/> (or the one registered under <paramref name="vectorStoreKey"/>);
    /// register the connector's store first.
    /// </summary>
    /// <param name="builder">The Akinly builder.</param>
    /// <param name="configure">Sets the store's options; <see cref="VectorDataClusterStoreOptions.Dimensions"/> is required.</param>
    /// <param name="vectorStoreKey">The key of a keyed <see cref="VectorStore"/>; null for the default one.</param>
    /// <exception cref="ArgumentException">The options are invalid.</exception>
    public static AkinlyBuilder UseVectorStore(this AkinlyBuilder builder, Action<VectorDataClusterStoreOptions> configure, object? vectorStoreKey = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new VectorDataClusterStoreOptions();
        configure(options);
        var errors = options.Validate();
        if (errors.Count > 0)
        {
            throw new ArgumentException("Invalid vector store options: " + string.Join(" ", errors), nameof(configure));
        }

        builder.Services.RemoveAll<IClusterStore>();
        builder.Services.AddSingleton<IClusterStore>(sp => new VectorDataClusterStore(
            vectorStoreKey is null ? sp.GetRequiredService<VectorStore>() : sp.GetRequiredKeyedService<VectorStore>(vectorStoreKey),
            options));
        return builder;
    }
}
