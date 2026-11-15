namespace Akinly;

/// <summary>A judge's answer to one question.</summary>
/// <param name="QuestionId">The question answered.</param>
/// <param name="Kind">The answer.</param>
public readonly record struct Verdict(QuestionId QuestionId, VerdictKind Kind)
{
    /// <summary>"The same": the item joins, or the clusters merge.</summary>
    public static Verdict Same(QuestionId questionId) => new(questionId, VerdictKind.Same);

    /// <summary>
    /// "The same", for a join question with <see cref="JoinQuestion.OtherChoices"/>: the item
    /// joins the cluster picked, <see cref="JoinQuestion.ClusterId"/> or one of the choices.
    /// </summary>
    public static Verdict Picked(QuestionId questionId, long clusterId) => new(questionId, VerdictKind.Same) { PickedClusterId = clusterId };

    /// <summary>
    /// The cluster picked from a join question's choices, or null for the question's own
    /// <see cref="JoinQuestion.ClusterId"/>. Ignored on any other answer, and on a merge question.
    /// </summary>
    public long? PickedClusterId { get; init; }

    /// <summary>"Different": the item is refused there, or the clusters stay apart.</summary>
    public static Verdict Different(QuestionId questionId) => new(questionId, VerdictKind.Different);

    /// <summary>No answer this time; the question is asked again.</summary>
    public static Verdict Failed(QuestionId questionId) => new(questionId, VerdictKind.Failed);

    /// <summary>No answer is possible; similarity alone decides.</summary>
    public static Verdict Unanswerable(QuestionId questionId) => new(questionId, VerdictKind.Unanswerable);
}
