using System.Globalization;
using System.Text.Json;
using Akinly.AI.Telemetry;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Akinly.AI;

/// <summary>
/// Titles clusters with a language model: one sentence naming what their members share. A
/// cluster gets a label once it reaches <see cref="ChatClientLabelerOptions.MinClusterSize"/>,
/// and a new one each time it grows or shrinks by <see cref="ChatClientLabelerOptions.RelabelGrowth"/>.
/// Judges see the label with their questions, so it also helps them decide.
/// </summary>
/// <remarks>
/// A label is accepted only if it is one line, not too long, and free of braces, quotes and
/// control characters (which could carry instructions into later prompts); a failed or unusable
/// answer leaves the old label in place. Requests carry no tools and no conversation state.
/// Labels come from the members' texts: review them before showing them publicly.
/// </remarks>
public sealed class ChatClientLabeler
{
    private static readonly JsonElement LabelSchema = JsonDocument.Parse(
        """{"type":"object","properties":{"label":{"type":"string"}},"required":["label"],"additionalProperties":false}""").RootElement.Clone();

    private readonly IChatClient _chat;
    private readonly IItemTextSource _texts;
    private readonly ChatClientLabelerOptions _options;
    private readonly ILogger _logger;

    /// <summary>A labeler over a chat client.</summary>
    /// <param name="chat">The model to ask.</param>
    /// <param name="texts">Where the items' texts are.</param>
    /// <param name="options">How to label; the defaults when null. Copied, so later changes have no effect.</param>
    /// <param name="logger">Where to log failures (their exception type, never the text); nowhere when null.</param>
    /// <exception cref="ArgumentException">The options are invalid.</exception>
    public ChatClientLabeler(IChatClient chat, IItemTextSource texts, ChatClientLabelerOptions? options = null, ILogger<ChatClientLabeler>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(texts);
        options ??= new ChatClientLabelerOptions();
        var errors = options.Validate();
        if (errors.Count > 0)
        {
            throw new ArgumentException("Invalid labeler options: " + string.Join(" ", errors), nameof(options));
        }

        _chat = chat;
        _texts = texts;
        _options = options.Clone();
        _logger = logger ?? NullLogger<ChatClientLabeler>.Instance;
    }

    /// <summary>
    /// Whether a cluster is due a label: big enough, and unlabeled, or grown or shrunk by
    /// <see cref="ChatClientLabelerOptions.RelabelGrowth"/> since its last label.
    /// </summary>
    /// <param name="cluster">The cluster.</param>
    public bool IsDue(Cluster cluster)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        if (cluster.Count < _options.MinClusterSize)
        {
            return false;
        }

        return cluster.Label is null
            || cluster.LabeledAtCount <= 0
            || cluster.Count >= cluster.LabeledAtCount * _options.RelabelGrowth
            || cluster.Count * _options.RelabelGrowth <= cluster.LabeledAtCount;
    }

    /// <summary>Labels the given clusters that are due a label, one at a time.</summary>
    /// <param name="clusterer">The clusterer that holds them.</param>
    /// <param name="clusterIds">The clusters to consider, such as those a step placed items in; merged ids lead to their survivors.</param>
    /// <param name="cancellationToken">Cancels the work; labels already set stay set.</param>
    /// <returns>The ids of the clusters that got a new label.</returns>
    public async Task<IReadOnlyList<long>> LabelAsync(Clusterer clusterer, IEnumerable<long> clusterIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clusterer);
        ArgumentNullException.ThrowIfNull(clusterIds);
        var labeled = new List<long>();
        var seen = new HashSet<long>();
        foreach (var id in clusterIds)
        {
            if (await clusterer.GetClusterAsync(id, cancellationToken).ConfigureAwait(false) is { } cluster
                && seen.Add(cluster.Id)
                && IsDue(cluster)
                && await LabelOneAsync(clusterer, cluster, cancellationToken).ConfigureAwait(false))
            {
                labeled.Add(cluster.Id);
            }
        }

        return labeled;
    }

    /// <summary>Labels every live cluster that is due a label.</summary>
    /// <param name="clusterer">The clusterer that holds them.</param>
    /// <param name="cancellationToken">Cancels the work; labels already set stay set.</param>
    /// <returns>The ids of the clusters that got a new label.</returns>
    public async Task<IReadOnlyList<long>> LabelAllAsync(Clusterer clusterer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clusterer);
        var due = new List<long>();
        await foreach (var cluster in clusterer.ListClustersAsync(cancellationToken).ConfigureAwait(false))
        {
            if (IsDue(cluster))
            {
                due.Add(cluster.Id);
            }
        }

        return await LabelAsync(clusterer, due, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes a label for texts, without storing it.</summary>
    /// <param name="memberTexts">Texts of the cluster's members, nearest its centroid first.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The label, or null when the model's answer was unusable.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="memberTexts"/> is null.</exception>
    /// <remarks>The chat client's own exceptions pass through; <see cref="LabelAllAsync"/> catches them.</remarks>
    public async Task<string?> WriteLabelAsync(IReadOnlyList<string> memberTexts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(memberTexts);
        var options = _options.ChatOptions?.Clone() ?? new ChatOptions();
        if (_options.UseZeroTemperature)
        {
            options.Temperature ??= 0f;
        }

        options.Tools = null;
        options.ToolMode = ChatToolMode.None;
        options.ConversationId = null;
        options.ContinuationToken = null;
        options.AllowBackgroundResponses = null;
        if (_options.UseStructuredOutput)
        {
            options.ResponseFormat = ChatResponseFormat.ForJsonSchema(LabelSchema, "label", "A title for the group of texts.");
        }

        var cut = memberTexts
            .Select(t => t.Length <= _options.MaxTextLength ? t : string.Concat(t.AsSpan(0, _options.MaxTextLength), "…"))
            .ToArray();
        ChatMessage[] messages =
        [
            new(ChatRole.System, _options.Instructions.Replace("{maxLength}", _options.MaxLength.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)),
            new(ChatRole.User, JsonSerializer.Serialize(new Dictionary<string, string[]> { ["texts"] = cut }, PromptJsonContext.Relaxed.DictionaryStringStringArray)),
        ];

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_options.RequestTimeout is { } limit)
        {
            timeout.CancelAfter(limit);
        }

        var response = await _chat.GetResponseAsync(messages, options, timeout.Token).ConfigureAwait(false);
        return Read(response.Text);
    }

    private async Task<bool> LabelOneAsync(Clusterer clusterer, Cluster cluster, CancellationToken cancellationToken)
    {
        string? label;
        try
        {
            var examples = await clusterer.Store.GetExamplesAsync(cluster.Id, _options.ExamplesPerLabel, cancellationToken).ConfigureAwait(false);
            var texts = new List<string>();
            foreach (var id in examples)
            {
                if (await _texts.GetTextAsync(id, cancellationToken).ConfigureAwait(false) is { } text)
                {
                    texts.Add(text);
                }
            }

            if (texts.Count == 0)
            {
                return false;
            }

            label = await WriteLabelAsync(texts, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // A failed label leaves the old one; it must not stop the others.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            Log.LabelFailed(_logger, cluster.Id, exception.GetType().Name);
            return false;
        }

        if (label is null)
        {
            Log.LabelRejected(_logger, cluster.Id);
            return false;
        }

        // The cluster may have merged into another while the model wrote: a label written for
        // the smaller part must not overwrite the survivor's.
        if ((await clusterer.GetClusterAsync(cluster.Id, cancellationToken).ConfigureAwait(false))?.Id != cluster.Id)
        {
            return false;
        }

        return await clusterer.SetLabelAsync(cluster.Id, label, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The label in a model's answer, or null when it is unusable.</summary>
    private string? Read(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // A thinking model may reason before its answer: only the answer is the label.
        var label = JudgePrompts.WithoutReasoning(text);
        if (label.StartsWith("```", StringComparison.Ordinal))
        {
            // Without structured output, models often fence the answer: ```json ... ```.
            var firstLineEnd = label.IndexOf('\n', StringComparison.Ordinal);
            label = firstLineEnd < 0 ? "" : label[(firstLineEnd + 1)..].Trim();
            if (label.EndsWith("```", StringComparison.Ordinal))
            {
                label = label[..^3].Trim();
            }
        }

        if (label.StartsWith('{'))
        {
            try
            {
                using var document = JsonDocument.Parse(label);
                label = document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("label", out var value)
                    && value.ValueKind == JsonValueKind.String
                        ? value.GetString() ?? ""
                        : "";
            }
            catch (JsonException)
            {
                return null;
            }
        }

        label = label.Trim().Trim('"', '\'', '`').Trim();
        var usable = label.Length > 0
            && label.Length <= _options.MaxLength
            && !label.Any(c => char.IsControl(c) || c is (char)0x2028 or (char)0x2029 or '{' or '}' or '"' or '`');
        return usable ? label : null;
    }
}
