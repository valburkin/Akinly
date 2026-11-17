using Microsoft.Extensions.AI;

namespace Akinly.AI;

/// <summary>How a <see cref="ChatClientJudge"/> asks its model.</summary>
public sealed class ChatClientJudgeOptions
{
    /// <summary>The default <see cref="Criterion"/>: the same specific problem or request, not merely the same topic.</summary>
    public const string DefaultCriterion =
        "about the same specific problem or request, so that one answer or fix would settle both; sharing a topic is not enough";

    /// <summary>
    /// What "the same" means for your data, completing "two texts belong together only when
    /// they are ...". The default fits feedback, tickets and complaints; for other data say
    /// what counts, such as "asking the same question" or "describing the same bug".
    /// </summary>
    public string Criterion { get; set; } = DefaultCriterion;

    /// <summary>
    /// The system prompt for yes-or-no join questions (those without choices; see
    /// <see cref="ChoiceInstructions"/>); <c>{criterion}</c> is replaced with
    /// <see cref="Criterion"/>. Null for the default, <see cref="JudgePrompts.DefaultJoinInstructions"/>.
    /// </summary>
    public string? JoinInstructions { get; set; }

    /// <summary>
    /// The system prompt for merge questions; <c>{criterion}</c> is replaced with
    /// <see cref="Criterion"/>. Null for the default, <see cref="JudgePrompts.DefaultMergeInstructions"/>.
    /// </summary>
    public string? MergeInstructions { get; set; }

    /// <summary>
    /// The system prompt for join questions with choices; <c>{criterion}</c> is replaced with
    /// <see cref="Criterion"/>. Null for the default, <see cref="JudgePrompts.DefaultChoiceInstructions"/>.
    /// </summary>
    public string? ChoiceInstructions { get; set; }

    /// <summary>
    /// Which mistake the model should rather make on a close call: <see cref="ClusteringPreference.Precision"/>
    /// tells it to keep texts apart when unsure, <see cref="ClusteringPreference.Recall"/> to put
    /// them together. Default <see cref="ClusteringPreference.Balanced"/>, no tie-break.
    /// The sentence is added after any instructions, the defaults or your own.
    /// </summary>
    public ClusteringPreference Preference { get; set; }

    /// <summary>
    /// Whether clusters' labels go into the prompts. Default true: a label shows the model what a
    /// reader would see. Turn it off if labels are not reviewed, so text from one item that ended
    /// up in a label cannot reach every later question about its cluster.
    /// </summary>
    public bool IncludeLabels { get; set; } = true;

    /// <summary>
    /// How a merge question is put to the model. Above 0, as join questions: whether each of the
    /// first this many members of one cluster (those nearest its centroid) belongs in the other,
    /// both ways, stopping at the first no; the clusters merge only if every answer is yes. 0
    /// asks once, comparing the two groups as a whole (<see cref="MergeInstructions"/>), as 1.0
    /// did. Default 1, at most two requests per merge question. A model places one text in a
    /// group far more reliably than it compares two groups, which on texts written to one
    /// template it tends to find alike. A question whose clusters show no members is asked as a
    /// whole.
    /// </summary>
    public int MergeMembersEachWay { get; set; } = 1;

    /// <summary>How many questions are put to the model at once. Default 4.</summary>
    public int MaxParallelRequests { get; set; } = 4;

    /// <summary>
    /// How long one request may take before it counts as a failed answer (asked again later).
    /// Default two minutes; null for no limit of the judge's own.
    /// </summary>
    public TimeSpan? RequestTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Texts longer than this many characters are cut, to bound a prompt's size. Default 1,000.</summary>
    public int MaxTextLength { get; set; } = 1000;

    /// <summary>
    /// Whether to ask for the answer through structured output (a JSON schema). Default true;
    /// turn it off for a provider that rejects response schemas. The answer is read either way.
    /// </summary>
    public bool UseStructuredOutput { get; set; } = true;

    /// <summary>
    /// Whether to send a temperature of 0 when <see cref="ChatOptions"/> sets none, for steadier
    /// answers. Default false: current Claude models and OpenAI's reasoning models reject any
    /// temperature. Turn it on for a model that accepts one (most local models do).
    /// </summary>
    public bool UseZeroTemperature { get; set; }

    /// <summary>
    /// Options passed with every request, such as the model id. Copied per request; the judge
    /// removes tools and conversation state from the copy.
    /// </summary>
    public ChatOptions? ChatOptions { get; set; }

    /// <summary>The problems with these options; empty when they are valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Criterion))
        {
            errors.Add("Criterion is required.");
        }

        if (!Enum.IsDefined(Preference))
        {
            errors.Add("Preference must be Balanced, Precision or Recall.");
        }

        if (MaxParallelRequests < 1)
        {
            errors.Add("MaxParallelRequests must be at least 1.");
        }

        if (MergeMembersEachWay < 0)
        {
            errors.Add("MergeMembersEachWay must be at least 0.");
        }

        if (MaxTextLength < 1)
        {
            errors.Add("MaxTextLength must be at least 1.");
        }

        if (RequestTimeout is { } timeout && (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue))
        {
            errors.Add("RequestTimeout must be positive and at most about 24 days; use null for no limit.");
        }

        return errors;
    }

    /// <summary>A copy, so a judge is not affected by later changes to the instance it was given.</summary>
    internal ChatClientJudgeOptions Clone() => new()
    {
        Criterion = Criterion,
        JoinInstructions = JoinInstructions,
        MergeInstructions = MergeInstructions,
        ChoiceInstructions = ChoiceInstructions,
        Preference = Preference,
        IncludeLabels = IncludeLabels,
        MergeMembersEachWay = MergeMembersEachWay,
        MaxParallelRequests = MaxParallelRequests,
        RequestTimeout = RequestTimeout,
        MaxTextLength = MaxTextLength,
        UseStructuredOutput = UseStructuredOutput,
        UseZeroTemperature = UseZeroTemperature,
        ChatOptions = ChatOptions?.Clone(),
    };
}
