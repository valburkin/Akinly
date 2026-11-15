namespace Akinly;

/// <summary>Loops for callers that answer questions live.</summary>
public static class ClustererExtensions
{
    // The longest wait between rounds, so a long outage keeps asking now and then rather than
    // backing off for hours (or past what Task.Delay accepts).
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Adds items, then puts every open question to the judge and applies the answers, round
    /// after round, until no question is left.
    /// </summary>
    /// <param name="clusterer">The clusterer.</param>
    /// <param name="items">The items to add.</param>
    /// <param name="judge">Answers the questions.</param>
    /// <param name="cancellationToken">Cancels the work between rounds; rounds already applied stay applied.</param>
    /// <returns>Everything the calls placed, merged and asked, as one step.</returns>
    public static async Task<ClusteringStep> SettleAsync(
        this Clusterer clusterer, IEnumerable<ClusterItem> items, IClusterJudge judge, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clusterer);
        var added = await clusterer.AddAsync(items, cancellationToken).ConfigureAwait(false);
        return added.Then(await clusterer.SettleAsync(judge, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Puts every open question to the judge and applies the answers, round after round, until
    /// no question is left. A question the judge leaves out counts as a failed answer, so the
    /// loop always ends: after <see cref="FailedAnswerOptions.MaxAttempts"/> failures similarity
    /// decides. After a round with a failed answer, it waits
    /// <see cref="FailedAnswerOptions.RetryDelay"/> before asking again; after rounds in which
    /// every answer failed, the wait doubles each time, up to five minutes (or the retry delay,
    /// if that is longer).
    /// </summary>
    /// <param name="clusterer">The clusterer.</param>
    /// <param name="judge">Answers the questions.</param>
    /// <param name="cancellationToken">Cancels the work between rounds; rounds already applied stay applied.</param>
    /// <returns>Everything the answers placed, merged and asked, as one step.</returns>
    public static async Task<ClusteringStep> SettleAsync(this Clusterer clusterer, IClusterJudge judge, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clusterer);
        ArgumentNullException.ThrowIfNull(judge);
        var retryDelay = clusterer.Options.FailedAnswers.RetryDelay;
        var maxDelay = retryDelay > MaxRetryDelay ? retryDelay : MaxRetryDelay;
        var delay = retryDelay;
        var total = ClusteringStep.Empty;
        while (true)
        {
            var open = await clusterer.GetOpenQuestionsAsync(cancellationToken).ConfigureAwait(false);
            if (open.Count == 0)
            {
                // Items may still wait for room to ask (after a removal, say): an empty answer
                // lets them in, and the loop goes on while that changes something.
                var waiting = (await clusterer.Store.GetInfoAsync(cancellationToken).ConfigureAwait(false)).WaitingItems;
                if (waiting == 0)
                {
                    return total;
                }

                var drained = await clusterer.AnswerAsync([], cancellationToken).ConfigureAwait(false);
                total = total.Then(drained);
                if (drained.Placements.Count == 0 && drained.Questions.Count == 0)
                {
                    return total;
                }

                continue;
            }

            var answered = (await judge.JudgeAsync(open, cancellationToken).ConfigureAwait(false))
                .GroupBy(v => v.QuestionId)
                .ToDictionary(g => g.Key, g => g.First());
            var verdicts = open.Select(q => answered.TryGetValue(q.Id, out var verdict) ? verdict : Verdict.Failed(q.Id)).ToList();
            total = total.Then(await clusterer.AnswerAsync(verdicts, cancellationToken).ConfigureAwait(false));

            // A failed answer is asked again next round: wait first, so a rate limit or a brief
            // outage has time to pass instead of using up every attempt at once. Only rounds in
            // which nothing got through make the wait grow.
            var failed = verdicts.Count(v => v.Kind == VerdictKind.Failed);
            if (failed == 0)
            {
                delay = retryDelay;
                continue;
            }

            var allFailed = failed == verdicts.Count;
            var wait = allFailed ? delay : retryDelay;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }

            delay = allFailed ? TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, maxDelay.Ticks)) : retryDelay;
        }
    }
}
