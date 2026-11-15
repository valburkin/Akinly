namespace Akinly;

/// <summary>A merge policy that decides each pair with a function.</summary>
/// <param name="decide">Decides one pair: the cluster with the lower id first.</param>
public sealed class DelegateMergePolicy(Func<Cluster, Cluster, CancellationToken, ValueTask<MergeRule>> decide) : IMergePolicy
{
    private readonly Func<Cluster, Cluster, CancellationToken, ValueTask<MergeRule>> _decide =
        decide ?? throw new ArgumentNullException(nameof(decide));

    /// <summary>A merge policy that decides each pair with a synchronous function.</summary>
    /// <param name="decide">Decides one pair: the cluster with the lower id first.</param>
    public DelegateMergePolicy(Func<Cluster, Cluster, MergeRule> decide)
        : this(Wrap(decide))
    {
    }

    /// <inheritdoc />
    public ValueTask<MergeRule> DecideAsync(Cluster first, Cluster second, CancellationToken cancellationToken) =>
        _decide(first, second, cancellationToken);

    private static Func<Cluster, Cluster, CancellationToken, ValueTask<MergeRule>> Wrap(Func<Cluster, Cluster, MergeRule> decide)
    {
        ArgumentNullException.ThrowIfNull(decide);
        return (first, second, _) => ValueTask.FromResult(decide(first, second));
    }
}
