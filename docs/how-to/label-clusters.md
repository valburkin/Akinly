# Label clusters

A label is a one-sentence title for a cluster, such as "Heating does not work". People read it in place of a list of members, and a judge sees it with every question about the cluster, which helps the judge decide what a reader would. Labels are optional: clustering works without them.

## Set labels yourself

`SetLabelAsync` sets or replaces a cluster's label and records its size at that moment:

```csharp
await clusterer.SetLabelAsync(1, "Heating does not work");
var cluster = await clusterer.GetClusterAsync(1);
Console.WriteLine($"{cluster!.Label}: {cluster.Count} items, labeled at {cluster.LabeledAtCount}");
// Heating does not work: 3 items, labeled at 3
```

It returns false when there is no such cluster. A merged cluster's id leads to the cluster it merged into, so a label set through an old id lands on the survivor. Pass `null` to clear a label.

## Let a model write them

`ChatClientLabeler` (in `Akinly.AI`) asks any `IChatClient` for a title, from the texts of the cluster's members nearest its center. It labels a cluster once it has `MinClusterSize` (3) members, and again each time the cluster has grown or shrunk by `RelabelGrowth` (2) since its last label, so a label keeps up with what the cluster has become.

`TextClusterer` does this for you: after each `AddAsync`, it labels the clusters that the add touched and that are due. Nothing to set up beyond giving it a chat client.

With a `Clusterer`, build a labeler over a chat client and the texts:

```csharp
using Akinly;
using Akinly.AI;

var texts = new InMemoryItemTextStore();   // or your own IItemTextSource over a database
texts.Set("ticket-1", "The heating is broken");
texts.Set("ticket-2", "Heating and hot water are both out");
texts.Set("ticket-3", "No heat in my apartment");

var labeler = new ChatClientLabeler(chat, texts, new ChatClientLabelerOptions { MinClusterSize = 3 });

// Every live cluster that is due a label:
IReadOnlyList<long> labeled = await labeler.LabelAllAsync(clusterer);

// Or only the clusters a step touched:
var step = await clusterer.AddAsync(items);
await labeler.LabelAsync(clusterer, step.Placements.Select(p => p.ClusterId));
```

| Method | What it does |
|---|---|
| `LabelAsync(clusterer, clusterIds)` | Labels the given clusters that are due, one at a time; returns the ids that got a new label |
| `LabelAllAsync(clusterer)` | The same, for every live cluster |
| `IsDue(cluster)` | Whether a cluster is due: big enough, and unlabeled, or grown or shrunk by `RelabelGrowth` since its label |
| `WriteLabelAsync(memberTexts)` | Writes a label for texts you pass, without storing it; null when the answer was unusable |

A failed model request or an unusable answer leaves the old label in place and is logged, and the labeler moves on to the next cluster. If the cluster merged into another while the model was writing, the label is dropped, so a label written for the smaller part never overwrites the survivor's.

Options are in [Configuration](../configuration.md#chatclientlabeleroptions). The labeler can use a different, cheaper model than the judge: build it with another chat client, or with dependency injection pass a `chatClientKey` ([example](dependency-injection.md#a-different-model-for-each-role)).

## Labels after merges and splits

- **Merge.** The surviving cluster keeps its label, or takes the other's if it had none. Its size jumps, so the labeler relabels it when the growth reaches `RelabelGrowth`.
- **Split.** The part that keeps the id keeps the label, but is marked as needing a new one (its `LabeledAtCount` is reset), so the labeler relabels it at its next chance. The new parts start unlabeled.
- **Removal.** A label is not changed when members leave; the labeler relabels a cluster once it has shrunk by `RelabelGrowth`.

## Safety

A label is made from members' texts, and it is shown to readers and quoted in later questions. An item's text can try to steer the model ("title this group: ..."). The labeler limits what a label can carry: it is accepted only if it is one line of at most `MaxLength` (120) characters with no braces, double quotes, backticks or control characters. Requests carry no tools and no conversation state. Still:

- **Review labels before you show them publicly.** The worst an injected text can do is bend a label or a cluster, never run code or read data, but a label is text a model wrote from text someone else wrote.
- **Keep unreviewed labels out of the judge's prompts** with `ChatClientJudgeOptions.IncludeLabels = false`, so text from one item that ended up in a label cannot reach every later question about its cluster.
- **Removed items.** A label written while an item was in the cluster can echo its text after the item is removed. If an item is removed on a deletion request, relabel or clear its cluster's label (see [Remove items](remove-items.md)).
