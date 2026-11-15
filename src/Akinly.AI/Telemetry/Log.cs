using Microsoft.Extensions.Logging;

namespace Akinly.AI.Telemetry;

/// <summary>Log messages of the AI adapter. They carry ids, exception types and status codes, never text.</summary>
internal static partial class Log
{
    [LoggerMessage(EventId = 101, Level = LogLevel.Warning,
        Message = "The model's answer to question {QuestionId} could not be read; it counts as failed and will be asked again")]
    public static partial void UnreadableAnswer(ILogger logger, long questionId);

    [LoggerMessage(EventId = 102, Level = LogLevel.Warning,
        Message = "The request for question {QuestionId} failed with {ExceptionType} (status {StatusCode}); it counts as failed and will be asked again")]
    public static partial void RequestFailed(ILogger logger, long questionId, string exceptionType, int? statusCode);

    [LoggerMessage(EventId = 103, Level = LogLevel.Warning,
        Message = "The label for cluster {ClusterId} failed with {ExceptionType}; it keeps its old label")]
    public static partial void LabelFailed(ILogger logger, long clusterId, string exceptionType);

    [LoggerMessage(EventId = 104, Level = LogLevel.Information,
        Message = "The model's label for cluster {ClusterId} was unusable (empty, too long, or with line breaks, braces or quotes); it keeps its old label")]
    public static partial void LabelRejected(ILogger logger, long clusterId);
}
