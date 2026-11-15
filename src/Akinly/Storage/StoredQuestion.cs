namespace Akinly.Storage;

/// <summary>A question as a store keeps it.</summary>
/// <param name="Question">The question.</param>
/// <param name="State">Open or closed.</param>
/// <param name="FailedAttempts">How many times a judge failed to answer it.</param>
/// <param name="Outcome">The answer that closed it, when one did.</param>
public sealed record StoredQuestion(Question Question, QuestionState State, int FailedAttempts, VerdictKind? Outcome);
