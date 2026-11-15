namespace Akinly.AI;

/// <summary>A text to cluster, with your id for it.</summary>
/// <param name="Id">Your id for the text, unique in the store.</param>
/// <param name="Text">The text: a short one works best, such as a sentence or a ticket's summary.</param>
public sealed record TextItem(string Id, string Text);
