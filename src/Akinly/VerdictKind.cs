namespace Akinly;

/// <summary>A judge's answer to a question.</summary>
public enum VerdictKind
{
    /// <summary>The same thing: the item joins, or the clusters merge.</summary>
    Same = 0,

    /// <summary>Different things: the item is refused there, or the clusters stay apart.</summary>
    Different = 1,

    /// <summary>
    /// No answer this time (a timeout, an outage, an answer that could not be read). The
    /// question stays open and is asked again, up to <see cref="FailedAnswerOptions.MaxAttempts"/>;
    /// a failure never counts as a refusal.
    /// </summary>
    Failed = 2,

    /// <summary>
    /// The judge cannot answer this question at all, for example because an item's text is
    /// gone. The question closes, and similarity alone decides.
    /// </summary>
    Unanswerable = 3,
}
