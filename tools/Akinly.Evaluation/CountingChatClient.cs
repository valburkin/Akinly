using Microsoft.Extensions.AI;

namespace Akinly.Evaluation;

/// <summary>Counts a chat client's requests, failed requests and tokens.</summary>
public sealed class CountingChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    private long _requests;
    private long _errors;
    private long _inputTokens;
    private long _outputTokens;

    public long Requests => Interlocked.Read(ref _requests);

    /// <summary>Requests that threw: the server down, a timeout, a refusal.</summary>
    public long Errors => Interlocked.Read(ref _errors);

    public long InputTokens => Interlocked.Read(ref _inputTokens);

    public long OutputTokens => Interlocked.Read(ref _outputTokens);

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _requests);
        try
        {
            var response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            Interlocked.Add(ref _inputTokens, response.Usage?.InputTokenCount ?? 0);
            Interlocked.Add(ref _outputTokens, response.Usage?.OutputTokenCount ?? 0);
            return response;
        }
        catch
        {
            Interlocked.Increment(ref _errors);
            throw;
        }
    }
}
