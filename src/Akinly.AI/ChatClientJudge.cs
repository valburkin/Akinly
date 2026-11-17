using Akinly.AI.Telemetry;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Akinly.AI;

/// <summary>
/// A judge that asks a language model through any <see cref="IChatClient"/>: a local model
/// (Ollama, for one) or a cloud one. A join question gets its own short request with the
/// cluster's label and its members nearest its center; a merge question, by default, two such
/// requests, each cluster's most central member against the other (see
/// <see cref="ChatClientJudgeOptions.MergeMembersEachWay"/>). The model answers
/// <c>{"same": true}</c> or <c>{"same": false}</c>.
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
        // The limit is per request: it starts again for each of a question's requests.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var limit = _options.RequestTimeout;
        if (limit is { } first)
        {
            timeout.CancelAfter(first);
        }

        try
        {
            var requests = await JudgePrompts.BuildRequestsAsync(question, _texts, _options, timeout.Token).ConfigureAwait(false);
            if (requests is null)
            {
                return Verdict.Unanswerable(question.Id);
            }

            // One by one, stopping at the first no: a no settles a merge asked by members, and
            // the verdict is the same as if every request had been sent.
            var choice = question is JoinQuestion { OtherChoices.Count: > 0 };
            var answers = new List<string?>();
            foreach (var messages in requests)
            {
                if (limit is { } each)
                {
                    timeout.CancelAfter(each);
                }

                var response = await _chat.GetResponseAsync(messages, JudgePrompts.CreateRequestOptions(_options, choice), timeout.Token).ConfigureAwait(false);
                answers.Add(response.Text);
                if (!choice && JudgePrompts.ReadAnswer(question, answers.Count - 1, response.Text) == false)
                {
                    break;
                }
            }

            var verdict = JudgePrompts.ReadVerdict(question, requests.Count, answers);
            if (verdict.Kind == VerdictKind.Failed)
            {
                Log.UnreadableAnswer(_logger, question.Id.Value);
            }

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
