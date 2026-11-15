namespace Akinly.AI;

/// <summary>How a <see cref="TextClusterer"/> runs the steps around the clusterer.</summary>
public sealed class TextClustererOptions
{
    /// <summary>How many texts go to the embedding model in one request. Default 64.</summary>
    public int EmbeddingBatchSize { get; set; } = 64;

    /// <summary>Whether each <see cref="TextClusterer.AddAsync"/> ends with a merge check. Default true.</summary>
    public bool MaintainAfterAdd { get; set; } = true;

    /// <summary>Whether clusters touched by an add are labeled when due (needs a labeler). Default true.</summary>
    public bool LabelClusters { get; set; } = true;

    /// <summary>
    /// How the judge asks its model, when the <see cref="TextClusterer"/> makes its own judge (the
    /// constructor that takes a chat client). With dependency injection, configure
    /// <c>UseChatClientJudge</c> instead.
    /// </summary>
    public ChatClientJudgeOptions Judge { get; set; } = new();

    /// <summary>
    /// How clusters are labeled, when the <see cref="TextClusterer"/> makes its own labeler (the
    /// constructor that takes a chat client). With dependency injection, configure
    /// <c>AddChatClientLabeler</c> instead.
    /// </summary>
    public ChatClientLabelerOptions Labeler { get; set; } = new();
}
