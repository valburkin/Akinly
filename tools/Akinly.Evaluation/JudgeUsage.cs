namespace Akinly.Evaluation;

/// <summary>What a judge did over a judged setup's runs, per item placed.</summary>
/// <param name="Judge">The judge model, as provider:model.</param>
/// <param name="QuestionsPerItem">Judge answers, retries of failed ones included.</param>
/// <param name="InputTokensPerItem">Prompt tokens.</param>
/// <param name="OutputTokensPerItem">Answer tokens.</param>
/// <param name="FailedShare">The share of answers that failed (the request, or reading the answer); a question failing every attempt is decided by similarity.</param>
/// <param name="UnanswerableShare">The share of verdicts with nothing fair to show the judge.</param>
/// <param name="RequestErrors">Requests that threw, in all.</param>
public sealed record JudgeUsage(
    string Judge, double QuestionsPerItem, double InputTokensPerItem, double OutputTokensPerItem,
    double FailedShare, double UnanswerableShare, long RequestErrors);
