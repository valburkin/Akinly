namespace Akinly.Evaluation;

/// <summary>A file of a public dataset, pinned to a commit and checked against its hash.</summary>
/// <param name="Url">Where it is downloaded from, at a fixed commit.</param>
/// <param name="Sha256">Its SHA-256, lowercase hex.</param>
/// <param name="FileName">Its name in the cache folder.</param>
public sealed record DatasetSource(string Url, string Sha256, string FileName);
