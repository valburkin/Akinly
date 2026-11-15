using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Akinly.Evaluation;

/// <summary>
/// Embeds texts once per model and server and keeps the vectors in the cache folder, so repeated
/// runs don't pay twice. Each vector is written to a temporary file and moved into place, so an
/// interrupted run never leaves a partial one; a cached file of the wrong size is embedded again.
/// </summary>
public static class Embeddings
{
    public static async Task<Dictionary<string, float[]>> GetAsync(
        IReadOnlyList<LabeledText> texts, string embedderSpec, string cacheDirectory, CancellationToken cancellationToken)
    {
        var folder = Folder(embedderSpec, Models.Endpoint(embedderSpec), cacheDirectory);
        Directory.CreateDirectory(folder);
        var vectors = new Dictionary<string, float[]>(StringComparer.Ordinal);
        var missing = new List<LabeledText>();
        int? dimensions = null;
        foreach (var text in texts)
        {
            var path = Path.Combine(folder, Key(text.Text) + ".bin");
            var vector = File.Exists(path) ? Read(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false)) : null;
            if (vector is not null && (dimensions ??= vector.Length) == vector.Length)
            {
                vectors[text.Id] = vector;
            }
            else
            {
                missing.Add(text);
            }
        }

        if (missing.Count > 0)
        {
            using var embedder = Models.Embedder(embedderSpec);
            foreach (var batch in missing.Chunk(64))
            {
                var embedded = await embedder.GenerateAsync(batch.Select(t => t.Text), cancellationToken: cancellationToken).ConfigureAwait(false);
                for (var i = 0; i < batch.Length; i++)
                {
                    var vector = embedded[i].Vector.ToArray();
                    if ((dimensions ??= vector.Length) != vector.Length)
                    {
                        throw new InvalidDataException($"The model returned {vector.Length} dimensions, but the cache holds {dimensions}: delete {folder} and embed again.");
                    }

                    vectors[batch[i].Id] = vector;
                    var path = Path.Combine(folder, Key(batch[i].Text) + ".bin");
                    var partial = $"{path}.{Guid.NewGuid():N}.partial";
                    await File.WriteAllBytesAsync(partial, Write(vector), cancellationToken).ConfigureAwait(false);
                    File.Move(partial, path, overwrite: true);
                }

                Console.Error.Write($"\rembedded {vectors.Count}/{texts.Count}");
            }

            Console.Error.WriteLine();
        }

        return vectors;
    }

    /// <summary>The cache folder of a model on a server: the same model name on another server is another model.</summary>
    internal static string Folder(string embedderSpec, string endpoint, string cacheDirectory) =>
        Path.Combine(cacheDirectory, "embeddings", Safe(embedderSpec) + "-" + Key(endpoint)[..8]);

    internal static byte[] Write(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        for (var i = 0; i < vector.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float)), vector[i]);
        }

        return bytes;
    }

    /// <summary>A cached vector, or null if the file is not a whole, non-empty vector.</summary>
    internal static float[]? Read(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length % sizeof(float) != 0)
        {
            return null;
        }

        var vector = new float[bytes.Length / sizeof(float)];
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * sizeof(float)));
        }

        return vector;
    }

    private static string Key(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];

    internal static string Safe(string text) => string.Concat(text.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' ? c : '_'));
}
