using System.Diagnostics.CodeAnalysis;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Akinly.AI;

/// <summary>Source-generated JSON for prompts: no reflection, safe to trim.</summary>
/// <remarks>Its code is generated, so it is left out of coverage; the prompt tests exercise it.</remarks>
[ExcludeFromCodeCoverage]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(Dictionary<string, string[]>))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(Dictionary<string, object?>[]))]
[JsonSerializable(typeof(int))]
internal sealed partial class PromptJsonContext : JsonSerializerContext
{
    /// <summary>
    /// Indented, and Unicode left readable (a model reads the prompt, not a browser); quotes and
    /// backslashes are still escaped, so the JSON stays valid and the fencing holds.
    /// </summary>
    public static PromptJsonContext Relaxed { get; } = new(new JsonSerializerOptions
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    });
}
