# Observe with OpenTelemetry

Akinly reports what it does through the .NET diagnostics APIs: traces through an `ActivitySource`, metrics through a `Meter`, and logs through `ILogger`. OpenTelemetry, or any listener, can collect them. **No trace, metric or log entry carries an item's text**: metric tags are low-cardinality words, and log messages carry counts, versions, ids, exception types and status codes.

## Names

Both the `ActivitySource` and the `Meter` are named `Akinly`, available as the constant `AkinlyTelemetry.Name` (namespace `Akinly.Telemetry`). Their version is the library's.

## Set up OpenTelemetry

In an ASP.NET Core app or a worker, with the `OpenTelemetry.Extensions.Hosting` package and an exporter, such as `OpenTelemetry.Exporter.OpenTelemetryProtocol` for OTLP:

```csharp
using Akinly.Telemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddSource(AkinlyTelemetry.Name)
        .AddOtlpExporter())
    .WithMetrics(metrics => metrics
        .AddMeter(AkinlyTelemetry.Name)
        .AddOtlpExporter());
```

Akinly's log messages go through the host's logging, like any other `ILogger` output.

## Traces

Each write operation of a `Clusterer` is one activity:

| Activity | Operation | Tags |
|---|---|---|
| `akinly.add` | `AddAsync` | `akinly.placed` (items placed), `akinly.questions` (questions asked) |
| `akinly.answer` | `AnswerAsync` | The same |
| `akinly.maintain` | `MaintainAsync` | The same |
| `akinly.remove` | `RemoveAsync` | The same |
| `akinly.label` | `SetLabelAsync` | The same |

`SettleAsync` and `TextClusterer.AddAsync` show up as the add, answer and maintain activities they run. Calls to the store, the embedding model and the chat model appear as child spans when their own libraries are instrumented. The read methods (`ListClustersAsync`, `GetClusterAsync` and the others) are not traced.

## Metrics

| Instrument | Type | Unit | Tags | What it counts |
|---|---|---|---|---|
| `akinly.items.placed` | Counter | `{item}` | `reason`: `similarity`, `judge`, `new_cluster` | Items placed in a cluster, and why |
| `akinly.questions.asked` | Counter | `{question}` | `kind`: `join`, `merge` | Questions put to a judge |
| `akinly.verdicts` | Counter | `{verdict}` | `kind`: `same`, `different`, `failed`, `unanswerable` | Answers passed to `AnswerAsync`, ignored ones included; a question a judge leaves out of a `SettleAsync` round counts as `failed` |
| `akinly.clusters.merged` | Counter | `{merge}` | `reason`: `similarity`, `judge` | Clusters merged into others |
| `akinly.clusters.split` | Counter | `{split}` | none | Loose clusters split |
| `akinly.store.conflicts` | Counter | `{conflict}` | none | Writes the store refused because another writer came first |
| `akinly.operation.duration` | Histogram | `s` | `operation`: `add`, `answer`, `maintain`, `remove`, `label` | How long a clusterer operation took, waiting for other calls on the same instance included |

What to watch:

- **`akinly.verdicts` with `kind=failed`.** A rising share means the judge is failing (a model down, timeouts, unreadable answers). Questions that fail every attempt are decided by similarity, which tends to leave more, smaller clusters. See [A judge that keeps failing](../troubleshooting.md#a-judge-that-keeps-failing).
- **Questions per item.** `akinly.questions.asked` divided by `akinly.items.placed` is what the judge costs you per item. A jump after a model or data change suggests the thresholds no longer fit.
- **`akinly.items.placed` by reason.** Mostly `new_cluster` means almost nothing joins: thresholds too high for the embedding model, or a judge that refuses too much.
- **`akinly.store.conflicts`.** Expected now and then with several clusterers on one in-memory store. With a vector database store, any conflict means a second writer: see [One writer](../concepts.md#one-writer).

## Logs

| Event id | Level | Category | Message |
|---|---|---|---|
| 1 | Debug | `Akinly.Clusterer` | `{Operation}: placed {Placed} items, merged {Merged} clusters, split {Split}, asked {Asked} questions; store version {Version}` |
| 2 | Information | `Akinly.Clusterer` | Another writer changed the store; reading again, with the attempt number |
| 3 | Warning | `Akinly.Clusterer` | Gave up after 16 conflicting writes |
| 101 | Warning | `Akinly.AI.ChatClientJudge` | The model's answer to a question could not be read; it counts as failed |
| 102 | Warning | `Akinly.AI.ChatClientJudge` | The request for a question failed, with the exception type and HTTP status; it counts as failed |
| 103 | Warning | `Akinly.AI.ChatClientLabeler` | A label request failed, with the exception type; the cluster keeps its old label |
| 104 | Information | `Akinly.AI.ChatClientLabeler` | The model's label was unusable; the cluster keeps its old label |

The clusterer logs through the `ILogger<Clusterer>` it is given (dependency injection passes one; `TextClusterer` takes an `ILoggerFactory`).

## The model calls

Akinly does not trace the model calls itself. Microsoft.Extensions.AI can: wrap the chat client with `UseOpenTelemetry()` from the `Microsoft.Extensions.AI` package, and add its source to tracing.

```csharp
using Microsoft.Extensions.AI;

IChatClient chat = new ChatClientBuilder(innerChatClient)
    .UseOpenTelemetry()
    .Build();
```

By default it records metadata such as token counts, not the messages. Its `EnableSensitiveData` option, or the `OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT=true` environment variable, adds the prompts and answers, which hold your items' texts: leave both off unless your telemetry may hold that data.

## Without OpenTelemetry

The instruments are plain `System.Diagnostics.Metrics`, so a `MeterListener` sees them too, for a test or a quick look:

```csharp
using System.Diagnostics.Metrics;
using Akinly.Telemetry;

using var listener = new MeterListener();
listener.InstrumentPublished = (instrument, meterListener) =>
{
    if (instrument.Meter.Name == AkinlyTelemetry.Name)
    {
        meterListener.EnableMeasurementEvents(instrument);
    }
};
listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
    Console.WriteLine($"{instrument.Name} +{value} {string.Join(", ", tags.ToArray().Select(t => $"{t.Key}={t.Value}"))}"));
listener.Start();

await clusterer.AddAsync([new ClusterItem("ticket-1", new float[] { 1.00f, 0.00f, 0.00f })]);
// akinly.items.placed +1 reason=new_cluster
```

The duration histogram reports `double` values; add `SetMeasurementEventCallback<double>` to see it.
