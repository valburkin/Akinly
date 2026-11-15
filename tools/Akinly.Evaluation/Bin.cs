namespace Akinly.Evaluation;

/// <summary>Of the candidates whose similarity falls in a range, how many were the right one.</summary>
/// <param name="From">The range's lower end (inclusive).</param>
/// <param name="To">The range's upper end (exclusive, but 1 is in the last range).</param>
/// <param name="Candidates">How many candidates fell in it.</param>
/// <param name="Right">How many of them were the right one.</param>
public sealed record Bin(double From, double To, int Candidates, int Right);
