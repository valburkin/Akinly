namespace Akinly.Storage;

/// <summary>An item as a store keeps it.</summary>
/// <param name="Id">The caller's id for the item.</param>
/// <param name="Sequence">The order it was added in, handed out by the store.</param>
/// <param name="Embedding">Its unit-length embedding.</param>
/// <param name="ClusterId">The cluster it is in, or null while it waits.</param>
/// <param name="State">Placed, asking or waiting.</param>
/// <param name="RefusedClusterIds">The clusters a judge refused it in, oldest first.</param>
/// <param name="OpenQuestionId">Its open join question, while it is asking.</param>
/// <param name="JudgeUnavailable">Whether similarity alone decides for it, because a judge could not answer.</param>
public sealed record StoredItem(
    string Id,
    long Sequence,
    ReadOnlyMemory<float> Embedding,
    long? ClusterId,
    ItemState State,
    IReadOnlyList<long> RefusedClusterIds,
    QuestionId? OpenQuestionId,
    bool JudgeUnavailable);
