using Akinly.AI.Telemetry;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Akinly.AI;

/// <summary>
/// A judge that asks a language model through any <see cref="IChatClient"/>: a local model
/// (Ollama, for one) or a cloud one. Each question gets its own short request with the
/// cluster's label and its members nearest its center; the model answers <c>{"same": true}</c> or
/// <c>{"same": false}</c>.
/// </summary>
/// <remarks>
/// <para>
/// It never throws for a failed question: a request that fails, exceeds
/// <see cref="ChatClientJudgeOptions.RequestTimeout"/>, returns an unreadable answer, or whose
/// texts cannot be read is a <see cref="VerdictKind.Failed"/> verdict, which the clusterer asks
/// again; a question with nothing fair to show (the item's text is gone, or the cluster has
/// neither a member's text nor a label) is <see cref="VerdictKind.Unanswerable"/>. Neither ever
/// becomes a refusal. Only cancellation by the caller throws.
/// </para>
/// <para>
/// The judge's requests carry no tools and no conversation state (see
/// <see cref="JudgePrompts.CreateRequestOptions"/>); middleware you add to the chat client can
/// still change a request. Safe to use from several threads.
/// </para>
/// </remarks>
public sealed class ChatClientJudge : IClusterJudge
{
    private readonly IChatClient _chat;
    private readonly IItemTextSource _texts;
    private readonly ChatClientJudgeOptions _options;
    private readonly ILogger _logger;

    /// <summary>A judge over a chat client.</summary>
    /// <param name="chat">The model to ask.</param>
    /// <param name="texts">Where the items' texts are.</param>
    /// <param name="options">How to ask; the defaults when null. Copied, so later changes have no effect.</param>
    /// <param name="logger">Where to log failed requests (their exception type and status, never the text); nowhere when null.</param>
    /// <exception cref="ArgumentException">The options are invalid.</exception>
    public ChatClientJudge(IChatClient chat, IItemTextSource texts, ChatClientJudgeOptions? options = null, ILogger<ChatClientJudge>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(texts);
        options ??= new ChatClientJudgeOptions();
        var errors = options.Validate();
        if (errors.Count > 0)
        {
            throw new ArgumentException("Invalid judge options: " + string.Join(" ", errors), nameof(options));
        }

        _chat = chat;
        _texts = texts;
        _options = options.Clone();
        _logger = logger ?? NullLogger<ChatClientJudge>.Instance;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Verdict>> JudgeAsync(IReadOnlyList<Question> questions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(questions);
        var verdicts = new Verdict[questions.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, questions.Count),
            new ParallelOptions { MaxDegreeOfParallelism = _options.MaxParallelRequests, CancellationToken = cancellationToken },
            async (index, token) => verdicts[index] = await AskAsync(questions[index], token).ConfigureAwait(false))
            .ConfigureAwait(false);
        return verdicts;
    }

    private async Task<Verdict> AskAsync(Question question, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_options.RequestTimeout is { } limit)
        {
            timeout.CancelAfter(limit);
        }

        try
        {
            var messages = await JudgePrompts.BuildAsync(question, _texts, _options, timeout.Token).ConfigureAwait(false);
            if (messages is null)
            {
                return Verdict.Unanswerable(question.Id);
            }

            var choice = question is JoinQuestion { OtherChoices.Count: > 0 };
            var response = await _chat.GetResponseAsync(messages, JudgePrompts.CreateRequestOptions(_options, choice), timeout.Token).ConfigureAwait(false);
            if (JudgePrompts.TryReadVerdict(question, response.Text, out var verdict))
            {
                return verdict;
            }

            Log.UnreadableAnswer(_logger, question.Id.Value);
            return verdict;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Any failure (a timeout, the model, the text store) is a failed answer, asked again later; it must not stop the others.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            Log.RequestFailed(_logger, question.Id.Value, exception.GetType().Name, (exception as HttpRequestException)?.StatusCode is { } status ? (int)status : null);
            return Verdict.Failed(question.Id);
        }
    }
}
