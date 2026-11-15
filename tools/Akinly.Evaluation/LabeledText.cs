namespace Akinly.Evaluation;

/// <summary>One labeled text.</summary>
/// <param name="Id">A unique id.</param>
/// <param name="Text">The text.</param>
/// <param name="Label">Its true group.</param>
public sealed record LabeledText(string Id, string Text, string Label);
