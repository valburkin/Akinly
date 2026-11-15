using Microsoft.Extensions.Logging;

namespace Akinly.Telemetry;

/// <summary>The library's log messages. They carry counts, ids and versions, never text.</summary>
internal static partial class Log
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
        Message = "{Operation}: placed {Placed} items, merged {Merged} clusters, split {Split}, asked {Asked} questions; store version {Version}")]
    public static partial void StepApplied(ILogger logger, string operation, int placed, int merged, int split, int asked, long version);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information,
        Message = "{Operation}: another writer changed the store (now at version {Version}); reading again, attempt {Attempt}")]
    public static partial void Conflict(ILogger logger, string operation, long version, int attempt);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
        Message = "{Operation}: gave up after {Attempts} conflicting writes; another writer keeps changing the store")]
    public static partial void GaveUp(ILogger logger, string operation, int attempts);
}
