namespace Akinly;

/// <summary>What happens when a judge fails to answer a question.</summary>
public sealed class FailedAnswerOptions
{
    /// <summary>
    /// How many failed answers a question takes before the clusterer gives up on it. Then a
    /// join is decided by similarity alone, as if no judge were configured, and a merge is not
    /// made. Default 3. A failure never counts as a refusal, so an outage delays placement
    /// without splitting clusters.
    /// </summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>
    /// How long <see cref="ClustererExtensions.SettleAsync(Clusterer, IEnumerable{ClusterItem}, IClusterJudge, CancellationToken)"/>
    /// waits before asking again after a round with a failed answer. After rounds in which every
    /// answer failed, the wait doubles each time, up to five minutes (or this delay, if longer).
    /// From zero to one hour; default one second.
    /// </summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);
}
