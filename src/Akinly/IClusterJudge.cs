namespace Akinly;

/// <summary>
/// Answers the questions the clusterer cannot settle by similarity: a language model, a rule,
/// or a person. Implementations must be safe to call from several threads if the caller
/// shares one.
/// </summary>
/// <remarks>
/// A join question may offer several clusters (<see cref="JoinQuestion.OtherChoices"/>, with the
/// default <see cref="ClusteringOptions.JoinChoices"/> of 3). Its <see cref="VerdictKind.Different"/>
/// means none of them, and every cluster offered refuses the item; pick one with
/// <see cref="Verdict.Picked"/>. A judge that only looks at the question's own cluster should
/// run with <c>JoinChoices = 1</c>.
/// </remarks>
public interface IClusterJudge
{
    /// <summary>Answers the questions.</summary>
    /// <param name="questions">The questions, in question order.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>
    /// One verdict per question it answered; a failure is a <see cref="VerdictKind.Failed"/>
    /// verdict, never an exception. A question left out stays open when you pass the verdicts
    /// to <see cref="Clusterer.AnswerAsync"/> yourself;
    /// <see cref="ClustererExtensions.SettleAsync(Clusterer, IClusterJudge, CancellationToken)"/>
    /// counts it as failed, so its loop always ends.
    /// </returns>
    Task<IReadOnlyList<Verdict>> JudgeAsync(IReadOnlyList<Question> questions, CancellationToken cancellationToken);
}
