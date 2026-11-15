namespace Akinly.Storage;

/// <summary>A store's metadata: its version, the embedding model it holds, and its counters.</summary>
/// <param name="Version">Moves up by one with every applied change set.</param>
/// <param name="EmbeddingModel">The model its embeddings come from, or null while it is empty.</param>
/// <param name="Dimensions">The length of its embeddings, or 0 while it is empty.</param>
/// <param name="NextClusterId">The id the next new cluster gets.</param>
/// <param name="NextQuestionId">The id the next question gets.</param>
/// <param name="NextItemSequence">The sequence number the next item gets.</param>
/// <param name="OpenQuestions">How many questions are open.</param>
/// <param name="WaitingItems">How many items wait to ask their question.</param>
/// <param name="MaintainCursor">
/// The id of the last changed cluster a merge check reached; the next check continues after it
/// and wraps around, so with a per-call limit every changed cluster gets its turn.
/// </param>
public sealed record StoreInfo(
    long Version,
    string? EmbeddingModel,
    int Dimensions,
    long NextClusterId,
    long NextQuestionId,
    long NextItemSequence,
    int OpenQuestions,
    int WaitingItems,
    long MaintainCursor = 0)
{
    /// <summary>The metadata of an empty store.</summary>
    public static StoreInfo Empty { get; } = new(0, null, 0, 1, 1, 1, 0, 0);
}
