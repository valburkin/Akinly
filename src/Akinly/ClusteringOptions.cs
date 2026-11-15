namespace Akinly;

/// <summary>
/// How the clusterer decides. The thresholds are cosine similarities and belong to an embedding
/// model: the defaults were measured with OpenAI's <c>text-embedding-3-small</c>; calibrate them
/// for another model.
/// </summary>
public sealed class ClusteringOptions
{
    /// <summary>The configuration section the options bind from by default.</summary>
    public const string SectionName = "Akinly";

    /// <summary>
    /// The model the embeddings come from, such as <c>text-embedding-3-small</c>. Required. The
    /// store records it, and a clusterer configured with another model refuses to work on that
    /// store, because thresholds do not carry over between models.
    /// </summary>
    public string EmbeddingModel { get; set; } = "";

    /// <summary>At or above this similarity to its nearest cluster, an item joins without a question. Default 0.83.</summary>
    public double JoinSimilarity { get; set; } = 0.83;

    /// <summary>
    /// At or above this similarity (and below <see cref="JoinSimilarity"/>), a judge decides
    /// whether an item joins. Default 0.30. Set it equal to <see cref="JoinSimilarity"/> for
    /// cosine-only clustering, with no judge.
    /// </summary>
    public double CheckSimilarity { get; set; } = 0.30;

    /// <summary>
    /// Refusals an item takes before it starts a cluster of its own; a "none" to a question with
    /// choices counts one for each cluster offered. Default 5.
    /// </summary>
    public int MaxChecksPerItem { get; set; } = 5;

    /// <summary>
    /// Questions open at once. While it is reached, new items that need a question wait. Lower
    /// is more accurate (an item placed while its neighbors' answers are out cannot see them),
    /// higher is faster. Default 200.
    /// </summary>
    public int MaxOpenQuestions { get; set; } = 200;

    /// <summary>
    /// Nearest clusters fetched from the store when placing an item (the nearest decides; the
    /// rest stand in when it is empty or refused the item) and for each cluster in a merge check.
    /// Default 10.
    /// </summary>
    public int CandidateClusters { get; set; } = 10;

    /// <summary>At or above this similarity of their centroids, two clusters merge without a question. Default 0.96.</summary>
    public double MergeSimilarity { get; set; } = 0.96;

    /// <summary>
    /// At or above this similarity (and below <see cref="MergeSimilarity"/>), a judge decides
    /// whether two clusters merge. Default 0.48.
    /// </summary>
    public double MergeCheckSimilarity { get; set; } = 0.48;

    /// <summary>
    /// Whether a merge is skipped when one cluster already refused an item that is now in the
    /// other. Default true: otherwise a merge can carry an item into a cluster that refused it.
    /// </summary>
    public bool MergeRespectsRefusals { get; set; } = true;

    /// <summary>
    /// The most changed clusters one <see cref="Clusterer.MaintainAsync"/> call checks; the rest
    /// stay marked changed for the next call, so one call never grows without bound. Default 1,000.
    /// </summary>
    public int MaxClustersPerMaintain { get; set; } = 1000;

    /// <summary>
    /// Clusters offered in one join question, nearest first: 1 asks yes or no about the nearest;
    /// more let the judge pick among them, or none, which shows it how fine the clusters are.
    /// Default 3.
    /// </summary>
    public int JoinChoices { get; set; } = 3;

    /// <summary>Members nearest the centroid shown to the judge with a join question. Default 10.</summary>
    public int ExamplesPerQuestion { get; set; } = 10;

    /// <summary>Members of each cluster shown to the judge with a merge question. Default 8.</summary>
    public int MergeExamplesPerCluster { get; set; } = 8;

    /// <summary>What happens when a judge fails to answer.</summary>
    public FailedAnswerOptions FailedAnswers { get; set; } = new();

    /// <summary>Splitting loose clusters, for cosine-only setups. Off by default.</summary>
    public SplitOptions Split { get; set; } = new();

    /// <summary>Whether the options leave a zone in which a judge decides joins or merges.</summary>
    public bool HasAskZone => CheckSimilarity < JoinSimilarity || MergeCheckSimilarity < MergeSimilarity;

    /// <summary>
    /// Options for cosine-only clustering: one threshold for joins and one for merges, no judge.
    /// </summary>
    /// <param name="embeddingModel">The model the embeddings come from.</param>
    /// <param name="joinSimilarity">The similarity at or above which an item joins its nearest cluster.</param>
    /// <param name="mergeSimilarity">The similarity at or above which two clusters merge; the join threshold when omitted.</param>
    public static ClusteringOptions CosineOnly(string embeddingModel, double joinSimilarity, double? mergeSimilarity = null) => new()
    {
        EmbeddingModel = embeddingModel,
        JoinSimilarity = joinSimilarity,
        CheckSimilarity = joinSimilarity,
        MergeSimilarity = mergeSimilarity ?? joinSimilarity,
        MergeCheckSimilarity = mergeSimilarity ?? joinSimilarity,
    };

    /// <summary>The problems with these options, one sentence each; empty when they are valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(EmbeddingModel))
        {
            errors.Add("EmbeddingModel is required: name the model the embeddings come from.");
        }

        Similarity(errors, JoinSimilarity, nameof(JoinSimilarity));
        Similarity(errors, CheckSimilarity, nameof(CheckSimilarity));
        Similarity(errors, MergeSimilarity, nameof(MergeSimilarity));
        Similarity(errors, MergeCheckSimilarity, nameof(MergeCheckSimilarity));
        if (CheckSimilarity > JoinSimilarity)
        {
            errors.Add("CheckSimilarity must not exceed JoinSimilarity.");
        }

        if (MergeCheckSimilarity > MergeSimilarity)
        {
            errors.Add("MergeCheckSimilarity must not exceed MergeSimilarity.");
        }

        AtLeast(errors, MaxChecksPerItem, 0, nameof(MaxChecksPerItem));
        AtLeast(errors, MaxOpenQuestions, 1, nameof(MaxOpenQuestions));
        AtLeast(errors, CandidateClusters, 1, nameof(CandidateClusters));
        AtLeast(errors, MaxClustersPerMaintain, 1, nameof(MaxClustersPerMaintain));
        AtLeast(errors, JoinChoices, 1, nameof(JoinChoices));
        AtLeast(errors, ExamplesPerQuestion, 0, nameof(ExamplesPerQuestion));
        AtLeast(errors, MergeExamplesPerCluster, 0, nameof(MergeExamplesPerCluster));

        if (FailedAnswers is null)
        {
            errors.Add("FailedAnswers is required.");
        }
        else
        {
            AtLeast(errors, FailedAnswers.MaxAttempts, 1, "FailedAnswers.MaxAttempts");
            if (FailedAnswers.RetryDelay < TimeSpan.Zero || FailedAnswers.RetryDelay > TimeSpan.FromHours(1))
            {
                errors.Add("FailedAnswers.RetryDelay must be from zero to one hour.");
            }
        }

        if (Split is null)
        {
            errors.Add("Split is required.");
        }
        else if (Split.Enabled)
        {
            Similarity(errors, Split.CohesionThreshold, "Split.CohesionThreshold");
            Similarity(errors, Split.RegroupSimilarity, "Split.RegroupSimilarity");
            AtLeast(errors, Split.MinPartSize, 1, "Split.MinPartSize");
            if (HasAskZone)
            {
                errors.Add("Split is for cosine-only setups: a judge approved each member, and a similarity number should not undo that. Turn it off, or set CheckSimilarity to JoinSimilarity and MergeCheckSimilarity to MergeSimilarity.");
            }
        }

        return errors;
    }

    /// <summary>Throws when the options are invalid.</summary>
    /// <exception cref="ArgumentException">The options are invalid; the message lists every problem.</exception>
    public void ThrowIfInvalid()
    {
        var errors = Validate();
        if (errors.Count > 0)
        {
            throw new ArgumentException("Invalid clustering options: " + string.Join(" ", errors));
        }
    }

    /// <summary>
    /// A deep copy: changes to it do not affect these options, nor the other way around. Missing
    /// nested options stay missing, for <see cref="Validate"/> to report.
    /// </summary>
    public ClusteringOptions Clone() => new()
    {
        EmbeddingModel = EmbeddingModel,
        JoinSimilarity = JoinSimilarity,
        CheckSimilarity = CheckSimilarity,
        MaxChecksPerItem = MaxChecksPerItem,
        MaxOpenQuestions = MaxOpenQuestions,
        CandidateClusters = CandidateClusters,
        MaxClustersPerMaintain = MaxClustersPerMaintain,
        MergeSimilarity = MergeSimilarity,
        MergeCheckSimilarity = MergeCheckSimilarity,
        MergeRespectsRefusals = MergeRespectsRefusals,
        JoinChoices = JoinChoices,
        ExamplesPerQuestion = ExamplesPerQuestion,
        MergeExamplesPerCluster = MergeExamplesPerCluster,
        FailedAnswers = FailedAnswers is null ? null! : new FailedAnswerOptions { MaxAttempts = FailedAnswers.MaxAttempts, RetryDelay = FailedAnswers.RetryDelay },
        Split = Split is null
            ? null!
            : new SplitOptions
            {
                Enabled = Split.Enabled,
                CohesionThreshold = Split.CohesionThreshold,
                RegroupSimilarity = Split.RegroupSimilarity,
                MinPartSize = Split.MinPartSize,
            },
    };

    private static void Similarity(List<string> errors, double value, string name)
    {
        if (!double.IsFinite(value) || value <= 0 || value > 1)
        {
            errors.Add($"{name} must be above 0 and at most 1.");
        }
    }

    private static void AtLeast(List<string> errors, int value, int minimum, string name)
    {
        if (value < minimum)
        {
            errors.Add($"{name} must be at least {minimum}.");
        }
    }
}
