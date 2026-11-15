using Akinly;
using Akinly.Storage;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Configures Akinly's services: the store, the judge, and the adapters' extensions.</summary>
/// <param name="services">The service collection.</param>
public sealed class AkinlyBuilder(IServiceCollection services)
{
    /// <summary>The service collection, for adapters to extend.</summary>
    public IServiceCollection Services { get; } = services ?? throw new ArgumentNullException(nameof(services));

    /// <summary>Keeps everything in an <see cref="InMemoryClusterStore"/>, one per container.</summary>
    public AkinlyBuilder UseInMemoryStore()
    {
        Services.RemoveAll<IClusterStore>();
        Services.AddSingleton<IClusterStore, InMemoryClusterStore>();
        return this;
    }

    /// <summary>Uses a store of your own, resolved from the container.</summary>
    /// <typeparam name="TStore">The store type.</typeparam>
    public AkinlyBuilder UseStore<TStore>()
        where TStore : class, IClusterStore
    {
        Services.RemoveAll<IClusterStore>();
        Services.AddSingleton<IClusterStore, TStore>();
        return this;
    }

    /// <summary>Uses a store instance.</summary>
    /// <param name="store">The store.</param>
    public AkinlyBuilder UseStore(IClusterStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        Services.RemoveAll<IClusterStore>();
        Services.AddSingleton(store);
        return this;
    }

    /// <summary>Uses a merge policy of your own, resolved from the container.</summary>
    /// <typeparam name="TPolicy">The policy type.</typeparam>
    public AkinlyBuilder UseMergePolicy<TPolicy>()
        where TPolicy : class, IMergePolicy
    {
        Services.RemoveAll<IMergePolicy>();
        Services.AddSingleton<IMergePolicy, TPolicy>();
        return this;
    }

    /// <summary>Uses a merge policy instance.</summary>
    /// <param name="policy">The policy.</param>
    public AkinlyBuilder UseMergePolicy(IMergePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        Services.RemoveAll<IMergePolicy>();
        Services.AddSingleton(policy);
        return this;
    }

    /// <summary>Uses a judge of your own, resolved from the container.</summary>
    /// <typeparam name="TJudge">The judge type.</typeparam>
    public AkinlyBuilder UseJudge<TJudge>()
        where TJudge : class, IClusterJudge
    {
        Services.RemoveAll<IClusterJudge>();
        Services.AddSingleton<IClusterJudge, TJudge>();
        return this;
    }

    /// <summary>Uses a judge instance.</summary>
    /// <param name="judge">The judge.</param>
    public AkinlyBuilder UseJudge(IClusterJudge judge)
    {
        ArgumentNullException.ThrowIfNull(judge);
        Services.RemoveAll<IClusterJudge>();
        Services.AddSingleton(judge);
        return this;
    }
}
