using Akinly;
using Akinly.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers Akinly in a service collection.</summary>
public static class AkinlyServiceCollectionExtensions
{
    /// <summary>
    /// Registers a singleton <see cref="Clusterer"/>, its options (validated when the host
    /// starts) and an <see cref="InMemoryClusterStore"/>; choose another store with the returned
    /// builder.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Sets the options.</param>
    public static AkinlyBuilder AddAkinly(this IServiceCollection services, Action<ClusteringOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = services.AddOptions<ClusteringOptions>();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        return Register(services, options);
    }

    /// <summary>
    /// Registers Akinly with its options bound from a configuration section, such as
    /// <c>configuration.GetSection("Akinly")</c>, then adjusted by <paramref name="configure"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The section to bind.</param>
    /// <param name="configure">Adjusts the options after binding.</param>
    public static AkinlyBuilder AddAkinly(this IServiceCollection services, IConfiguration configuration, Action<ClusteringOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var options = services.AddOptions<ClusteringOptions>().Bind(configuration);
        if (configure is not null)
        {
            options.Configure(configure);
        }

        return Register(services, options);
    }

    private static AkinlyBuilder Register(IServiceCollection services, OptionsBuilder<ClusteringOptions> options)
    {
        options.ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ClusteringOptions>, ClusteringOptionsValidator>());
        services.TryAddSingleton<IClusterStore, InMemoryClusterStore>();
        services.TryAddSingleton(sp => new Clusterer(
            sp.GetRequiredService<IClusterStore>(),
            sp.GetRequiredService<IOptions<ClusteringOptions>>().Value,
            sp.GetService<ILogger<Clusterer>>(),
            sp.GetService<IMergePolicy>()));
        return new AkinlyBuilder(services);
    }
}
