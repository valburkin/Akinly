# Keep chosen clusters apart with a merge policy

Maintenance merges clusters that turn out to be one: by similarity, or after a judge says yes. Sometimes you know more than similarity or a judge can: two clusters belong to different tenants or languages, a person split them on purpose, or one has a public page whose address should outlive the other. An `IMergePolicy` tells the `Clusterer` these rules.

- [How it works](#how-it-works)
- [A policy as a function](#a-policy-as-a-function)
- [A policy as a class](#a-policy-as-a-class)
- [With dependency injection or TextClusterer](#with-dependency-injection-or-textclusterer)
- [Things to know](#things-to-know)

## How it works

The clusterer asks the policy about a pair before every merge, certain or judged, and before it asks a judge a merge question. The policy gets the two clusters (`Cluster`: id, count, label, centroid), the one with the lower id first, and answers with a `MergeRule`:

| Rule | What happens |
|---|---|
| `MergeRule.Allow` | The default: the pair may merge, and the larger keeps its id (on a tie, the lower id) |
| `MergeRule.Never` | The pair never merges, whatever its similarity or a judge's answer, and no merge question is asked about it |
| `MergeRule.KeepFirst` | The pair may merge, and the first (the lower id) keeps its id |
| `MergeRule.KeepSecond` | The pair may merge, and the second (the higher id) keeps its id |

A policy decides merges only. Joins, refusals and the zones work as before.

## A policy as a function

`DelegateMergePolicy` wraps a function. Here cluster 1 holds two tickets and cluster 2 one; their centroids are 0.87 similar, at or above the merge threshold of 0.85. Without a policy, cluster 2 would merge into the larger cluster 1. Cluster 2 has a published page, so the policy lets it keep its id:

```csharp
using Akinly;
using Akinly.Storage;

var published = new HashSet<long> { 2 };   // your own data: clusters with a public page
var policy = new DelegateMergePolicy((first, second) =>
    published.Contains(second.Id) && !published.Contains(first.Id) ? MergeRule.KeepSecond
    : published.Contains(first.Id) && !published.Contains(second.Id) ? MergeRule.KeepFirst
    : MergeRule.Allow);

var options = ClusteringOptions.CosineOnly("toy-3d", joinSimilarity: 0.9, mergeSimilarity: 0.85);
var clusterer = new Clusterer(new InMemoryClusterStore(), options, mergePolicy: policy);
await clusterer.AddAsync(
[
    new ClusterItem("ticket-1", new float[] { 1.00f, 0.00f, 0.00f }),
    new ClusterItem("ticket-2", new float[] { 1.00f, 0.02f, 0.00f }),
    new ClusterItem("ticket-3", new float[] { 0.87f, 0.50f, 0.00f }),   // 0.87 similar: starts cluster 2
]);

var step = await clusterer.MaintainAsync();
Console.WriteLine(step.Merges[0]);   // ClusterMerge { FromId = 1, IntoId = 2, Reason = Similarity }
```

Answer `MergeRule.Never` instead, and the two clusters stay apart for good, with no merge question asked about them.

`DelegateMergePolicy` also takes an async function, `(first, second, cancellationToken) => ValueTask<MergeRule>`, for a policy that looks things up.

## A policy as a class

Implement `IMergePolicy.DecideAsync`. This one keeps clusters of different tenants apart, with the tenant of each cluster in your own data:

```csharp
public sealed class TenantMergePolicy(ITenantDirectory tenants) : IMergePolicy
{
    public async ValueTask<MergeRule> DecideAsync(Cluster first, Cluster second, CancellationToken cancellationToken) =>
        await tenants.TenantOfClusterAsync(first.Id, cancellationToken) == await tenants.TenantOfClusterAsync(second.Id, cancellationToken)
            ? MergeRule.Allow
            : MergeRule.Never;
}
```

`ITenantDirectory` stands for your own code. Pass the policy to the constructor: `new Clusterer(store, options, mergePolicy: new TenantMergePolicy(tenants))`.

## With dependency injection or TextClusterer

With dependency injection, register the policy on the builder, and the container's `Clusterer` (and a `TextClusterer` from `AddTextClusterer`) uses it:

```csharp
builder.Services.AddAkinly(builder.Configuration.GetSection(ClusteringOptions.SectionName))
    .UseMergePolicy<TenantMergePolicy>();   // or .UseMergePolicy(policyInstance)
```

Without dependency injection, `TextClusterer`'s constructor that takes a chat client builds its own `Clusterer`, with no policy. To use one, build the `Clusterer` yourself and pass it to the constructor that takes a `Clusterer`, with the embedding generator, text store, judge and labeler.

## Things to know

- **Keep it fast and free of side effects.** The policy runs inside the clusterer's calls, possibly several times for one pair and from several threads.
- **Its answers can change.** A pair is asked again in each call. A judge's yes that arrives after the policy started answering `Never` does not merge the pair.
- **An exception fails the call.** An exception the policy throws, or an answer that is not a `MergeRule`, fails the `MaintainAsync` or `AnswerAsync` call that asked.
- **A merge it forbids is not recorded as "different".** Answer `Allow` again later, and the pair can merge when maintenance next checks one of the two clusters, after either changes.
