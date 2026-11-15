using System.Text.Json.Serialization;

namespace Akinly;

/// <summary>
/// A question the clusterer cannot settle by similarity alone: whether an item belongs in a
/// cluster (<see cref="JoinQuestion"/>), or whether two clusters are one
/// (<see cref="MergeQuestion"/>). A question carries everything a judge needs besides the
/// texts themselves, so it can be answered later, elsewhere, or in a provider's batch.
/// </summary>
/// <param name="Id">The question's id; an answer refers to it.</param>
/// <param name="Similarity">The cosine similarity that put the question in the ask zone.</param>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(JoinQuestion), "join")]
[JsonDerivedType(typeof(MergeQuestion), "merge")]
public abstract record Question(QuestionId Id, double Similarity);
