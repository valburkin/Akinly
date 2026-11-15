using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace Akinly.Telemetry;

/// <summary>
/// The library's traces and metrics, both named <see cref="Name"/>. Tags are low-cardinality
/// words, never ids or text.
/// </summary>
public static class AkinlyTelemetry
{
    /// <summary>The name of the <see cref="ActivitySource"/> and the <see cref="Meter"/>: subscribe to it.</summary>
    public const string Name = "Akinly";

    private static readonly string? Version =
        typeof(AkinlyTelemetry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    internal static readonly ActivitySource Source = new(Name, Version);
    internal static readonly Meter Meter = new(Name, Version);

    internal static readonly Counter<long> ItemsPlaced =
        Meter.CreateCounter<long>("akinly.items.placed", "{item}", "Items placed in a cluster, by reason.");

    internal static readonly Counter<long> QuestionsAsked =
        Meter.CreateCounter<long>("akinly.questions.asked", "{question}", "Questions put to a judge, by kind.");

    internal static readonly Counter<long> Verdicts =
        Meter.CreateCounter<long>("akinly.verdicts", "{verdict}", "Answers passed to the clusterer, by kind.");

    internal static readonly Counter<long> ClustersMerged =
        Meter.CreateCounter<long>("akinly.clusters.merged", "{merge}", "Clusters merged into others, by reason.");

    internal static readonly Counter<long> ClustersSplit =
        Meter.CreateCounter<long>("akinly.clusters.split", "{split}", "Loose clusters split into parts.");

    internal static readonly Counter<long> Conflicts =
        Meter.CreateCounter<long>("akinly.store.conflicts", "{conflict}", "Writes the store refused because another writer came first.");

    internal static readonly Histogram<double> Duration =
        Meter.CreateHistogram<double>("akinly.operation.duration", "s", "How long a clusterer operation took, by operation.");

    internal static string Tag(PlacementReason reason) => reason switch
    {
        PlacementReason.Similarity => "similarity",
        PlacementReason.Judge => "judge",
        _ => "new_cluster",
    };

    internal static string Tag(VerdictKind kind) => kind switch
    {
        VerdictKind.Same => "same",
        VerdictKind.Different => "different",
        VerdictKind.Failed => "failed",
        _ => "unanswerable",
    };

    internal static string Tag(Question question) => question is MergeQuestion ? "merge" : "join";
}
