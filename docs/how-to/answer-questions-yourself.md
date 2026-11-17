# Answer questions yourself or through a batch API

The `Clusterer` never has to call a model itself. When similarity cannot decide, it hands back a question as data and takes the answer whenever it arrives: from your own code, from a person reviewing a queue, or from a provider's batch API hours later. This guide shows each.

- [Questions as data](#questions-as-data)
- [Questions with choices](#questions-with-choices)
- [Answer live with your own judge](#answer-live-with-your-own-judge)
- [A person in the loop](#a-person-in-the-loop)
- [Through a provider's batch API](#through-a-providers-batch-api)
- [Things to know](#things-to-know)

## Questions as data

With options that have an ask zone (the defaults: join at 0.83, ask from 0.30), an item in the ask zone does not join right away. `AddAsync` returns a question instead, and the item waits:

```csharp
using Akinly;
using Akinly.Storage;

var store = new InMemoryClusterStore();
var clusterer = new Clusterer(store, new ClusteringOptions { EmbeddingModel = "toy-3d" });   // join at 0.83, ask from 0.30

var step = await clusterer.AddAsync(
[
    new ClusterItem("ticket-1", new float[] { 1.00f, 0.00f, 0.00f }),
    new ClusterItem("ticket-2", new float[] { 0.82f, 0.57f, 0.00f }),   // 0.82 similar to ticket-1: a close call
]);

foreach (var question in step.Questions.OfType<JoinQuestion>())
{
    Console.WriteLine($"question {question.Id}: does {question.ItemId} belong in cluster {question.ClusterId}? " +
        $"similarity {question.Similarity:0.00}, examples: {string.Join(", ", question.ExampleItemIds)}");
}
// question 1: does ticket-2 belong in cluster 1? similarity 0.82, examples: ticket-1

Console.WriteLine(await clusterer.GetClusterOfAsync("ticket-2"));   // empty: null while the question is open
```

The question names the item, the cluster, the cluster's label (none yet) and the members nearest its center, by id. Look the texts up in your own data, decide, and answer. The answer can come in the same process or in another one working on the same store, minutes or days later:

```csharp
var open = await clusterer.GetOpenQuestionsAsync();
var answered = await clusterer.AnswerAsync([Verdict.Same(open[0].Id)]);
Console.WriteLine(answered.Placements[0]);
// Placement { ItemId = ticket-2, ClusterId = 1, Reason = Judge }
```

Had the answer been `Verdict.Different(...)`, cluster 1 would have refused `ticket-2`, and with no other cluster near, it would have started cluster 2 (`Reason = NewCluster`). The answers:

| Verdict | Use it when |
|---|---|
| `Verdict.Same(id)` | The item belongs in the cluster (the question's `ClusterId`), or the two clusters are one |
| `Verdict.Picked(id, clusterId)` | For a join question with choices: the item belongs in that cluster, the question's `ClusterId` or one of `OtherChoices` |
| `Verdict.Different(id)` | They are different things: the item belongs in none of the clusters offered, and tries its next nearest cluster; the clusters are never merged |
| `Verdict.Failed(id)` | You could not answer this time (a timeout, an error, an unreadable answer). The question stays open; after `FailedAnswers.MaxAttempts` (3) failures, similarity decides |
| `Verdict.Unanswerable(id)` | The question cannot be answered at all, such as when a text is gone. Similarity decides at once |

An answer to an unknown or already answered question is ignored, so answers can arrive late, out of order, or twice. [Verdicts](../concepts.md#verdicts) has the details.

`MaintainAsync` asks `MergeQuestion`s the same way: "are clusters 3 and 7 one?", with examples from both. Answer them with `Same` or `Different`.

## Questions with choices

When more than one cluster is near, a join question offers several: its own `ClusterId` (the nearest) and, in `OtherChoices`, up to `JoinChoices - 1` (by default 2) more, nearest first. Each choice is a `JoinChoice(ClusterId, Similarity, ClusterLabel, ExampleItemIds)`. The nearest is at or above `CheckSimilarity`; the others are there as context, whatever their similarity, so whoever answers sees how fine the clusters around the item are.

Answer with the one cluster the item belongs in, or with none:

```csharp
foreach (var question in step.Questions.OfType<JoinQuestion>())
{
    long? pick = Decide(question);   // your code: question.ClusterId, a choice's ClusterId, or null for none
    var verdict = pick switch
    {
        null => Verdict.Different(question.Id),                // none of them: every cluster offered refuses the item
        { } clusterId => Verdict.Picked(question.Id, clusterId), // join the cluster picked
    };
    await clusterer.AnswerAsync([verdict]);
}
```

- `Verdict.Same(id)` is the same as picking the question's own `ClusterId`.
- `Verdict.Different(id)` counts as a refusal by every cluster offered, and each counts toward `MaxChecksPerItem` (3). The item then tries its next nearest clusters that have not refused it.
- A pick of a cluster that was not offered counts as `Failed`: the question stays open.
- To ask only yes or no, set `JoinChoices` to 1; `OtherChoices` is then always empty.

## Answer live with your own judge

A judge is anything that implements `IClusterJudge`: given questions, return verdicts. `DelegateJudge` wraps a function, and `SettleAsync` loops add, judge and answer until no question is left. This judge is a rule: two tickets are the same only if they are about the same product.

```csharp
var product = new Dictionary<string, string>
{
    ["ticket-1"] = "boiler",
    ["ticket-2"] = "boiler",
};

// Two tickets are the same only if they are about the same product. A join question offers
// up to three clusters, nearest first: pick the first whose example has the item's product.
var judge = new DelegateJudge(question =>
{
    if (question is not JoinQuestion join)
    {
        return Verdict.Unanswerable(question.Id);   // merges: similarity decides
    }

    var offered = join.OtherChoices.Select(c => (c.ClusterId, c.ExampleItemIds)).Prepend((join.ClusterId, join.ExampleItemIds));
    foreach (var (clusterId, examples) in offered)
    {
        if (examples.Count > 0 && product[examples[0]] == product[join.ItemId])
        {
            return Verdict.Picked(join.Id, clusterId);
        }
    }

    return Verdict.Different(join.Id);   // none of them
});

var clusterer = new Clusterer(new InMemoryClusterStore(), new ClusteringOptions { EmbeddingModel = "toy-3d" });
var step = await clusterer.SettleAsync(
[
    new ClusterItem("ticket-1", new float[] { 1.00f, 0.00f, 0.00f }),
    new ClusterItem("ticket-2", new float[] { 0.82f, 0.57f, 0.00f }),
], judge);

foreach (var placement in step.Placements)
{
    Console.WriteLine($"{placement.ItemId} -> cluster {placement.ClusterId} ({placement.Reason})");
}
// ticket-1 -> cluster 1 (NewCluster)
// ticket-2 -> cluster 1 (Judge)
```

`DelegateJudge` also takes an async function, `(question, cancellationToken) => ValueTask<Verdict>`, for a judge that looks things up, and functions that return only a `VerdictKind`. With a `VerdictKind`, a question with choices can't pick another cluster: `Same` picks the question's own `ClusterId`, and `Different` refuses every cluster offered. An exception the function throws counts as a failed answer. To write a judge as a class, implement `IClusterJudge.JudgeAsync`: return one verdict per question, and report a failure as `VerdictKind.Failed` rather than throwing. `SettleAsync` counts a question your judge leaves out as failed, so its loop always ends.

`SettleAsync(judge)` without items settles whatever is open, such as the merge questions of a `MaintainAsync`:

```csharp
await clusterer.MaintainAsync();
await clusterer.SettleAsync(judge);
```

For a language model as the judge, use `ChatClientJudge` from `Akinly.AI` ([Ollama](use-ollama.md), [OpenAI and others](use-openai.md)).

## A person in the loop

People can be the judge. Keep the questions in your own queue (a table, a ticketing system), show the reviewer the texts behind the ids, and pass each decision to `AnswerAsync` when it is made:

1. After each `AddAsync`, `AnswerAsync` or `MaintainAsync`, store the step's `Questions` (or read `GetOpenQuestionsAsync()` whenever you like; it returns every open question).
2. For a `JoinQuestion`, show the new item's text, the cluster's label, and the texts of `ExampleItemIds`, and the same for each of `OtherChoices`; the reviewer picks one cluster or none. For a `MergeQuestion`, show both labels and both lists of examples.
3. Pass each decision as a verdict. An answer can make a follow-up question (an item refused by its nearest cluster is asked about the next): `AnswerAsync` returns it in its step's `Questions`.

While questions wait, the items they are about stay out of every cluster, and at most `MaxOpenQuestions` (200) are open at once; more items wait their turn. Size the limit to what your reviewers can work through.

## Through a provider's batch API

Several model providers run batch jobs: you upload many requests at once and collect the answers later, often at a lower price than live calls. `JudgePrompts` (in `Akinly.AI`) builds exactly the requests `ChatClientJudge` would send and reads the answers back, so you can send the questions as a batch and answer them when the batch ends. `BuildRequestsAsync` returns every request a question needs: one for a join question, and for a merge question one for each member asked each way (`MergeMembersEachWay`, default 1, so two). Send them all in the same batch. `ReadVerdict` reads a question's answers, in any order, into one verdict: for a merge, any no is `Different`, all yes is `Same`. A join question with choices gets its own prompt (`ChoiceInstructions`) and answer format (`{"group": n}`), so build its request options with `choice: true`.

```csharp
using System.Globalization;
using Akinly;
using Akinly.AI;
using Microsoft.Extensions.AI;

// The same settings a live judge would use: criterion, text length, labels.
var judgeOptions = new ChatClientJudgeOptions { Criterion = ChatClientJudgeOptions.DefaultCriterion };

// 1. Build the requests for each open question not already sent: one for a join question,
//    two for a merge question (one member of each cluster, asked about the other).
var requests = new List<(string CustomId, IList<ChatMessage> Messages, ChatOptions Settings)>();
var unanswerable = new List<Verdict>();
foreach (var question in await clusterer.GetOpenQuestionsAsync())
{
    if (sent.Contains(question.Id))
    {
        continue;   // already in a batch that has not ended
    }

    var built = await JudgePrompts.BuildRequestsAsync(question, texts, judgeOptions);
    if (built is null)
    {
        unanswerable.Add(Verdict.Unanswerable(question.Id));   // a text is gone: similarity decides
        continue;
    }

    var choice = question is JoinQuestion { OtherChoices.Count: > 0 };   // answered {"group": n}, not {"same": ...}
    for (var part = 0; part < built.Count; part++)
    {
        // The question, the request's place and how many requests the question has.
        var customId = string.Create(CultureInfo.InvariantCulture, $"{question.Id.Value}-{part}-{built.Count}");
        requests.Add((customId, built[part], JudgePrompts.CreateRequestOptions(judgeOptions, choice)));
    }

    sent.Add(question.Id);
}

await clusterer.AnswerAsync(unanswerable);

// 2. Send them in your provider's batch format. Each request: the custom id, the model, the
//    messages (message.Role.Value and message.Text), and its settings. Settings.ResponseFormat
//    holds the answer's JSON schema: JudgePrompts.AnswerSchema ({"same": boolean}), or
//    JudgePrompts.ChoiceAnswerSchema ({"group": integer}) for a question with choices.
await SubmitBatchAsync(requests);   // your code, for your provider

// 3. When the batch has ended, gather each question's answers and pass the verdicts back.
var open = (await clusterer.GetOpenQuestionsAsync()).ToDictionary(q => q.Id);
var answers = new Dictionary<QuestionId, (int RequestCount, List<string?> Replies)>();
foreach (var (customId, text) in await ReadBatchResultsAsync())   // your code: the custom id and the model's text, or null on an error
{
    var fields = customId.Split('-');
    var id = new QuestionId(long.Parse(fields[0], CultureInfo.InvariantCulture));
    sent.Remove(id);
    if (!answers.TryGetValue(id, out var entry))
    {
        answers[id] = entry = (int.Parse(fields[2], CultureInfo.InvariantCulture), []);
    }

    entry.Replies.Add(text);
}

var verdicts = new List<Verdict>();
foreach (var (id, (requestCount, replies)) in answers)
{
    if (open.TryGetValue(id, out var question))
    {
        // An error, an unreadable answer or a request that never came back reads as Failed,
        // unless another answer says no: the question stays open for the next batch.
        verdicts.Add(JudgePrompts.ReadVerdict(question, requestCount, replies));
    }
}

var step = await clusterer.AnswerAsync(verdicts);
// step.Questions holds any follow-up questions: send them in the next batch.
```

Here `clusterer` is your `Clusterer`, `texts` your `IItemTextSource` (the texts by item id), and `sent` a `HashSet<QuestionId>` you keep between runs, so a question in a batch that has not ended is not sent twice (when a batch ends, also remove the questions none of whose requests came back). `SubmitBatchAsync` and `ReadBatchResultsAsync` stand for your provider's batch client.

How this differs from a live judge:

- **Answers are matched by question id**, so the order of results does not matter, and a result for a question already answered is ignored.
- **Raise `MaxOpenQuestions`** if you want large batches: at most that many questions are open at once (default 200), and the items that would ask more wait until answers make room. Lower values are more accurate, because an item placed while its neighbors' answers are out cannot see where they went; a batch trades some of that for price.
- **Run `MaintainAsync`** after each batch of answers, and send its merge questions in the next batch.
- **A merge is asked by its members.** With the default `MergeMembersEachWay = 1`, a merge question becomes two yes-or-no join requests: whether the most central member of each cluster belongs in the other. Send both in the same batch: `ReadVerdict` needs a yes to every request for a `Same` (it is told how many there were, so one that never came back is never taken for a yes), and a single no is enough for a `Different`. Or, to pay for fewer requests (most merge questions are answered no), send them one after another: read each answer with `ReadAnswer(question, index, answer)`, send the next on a yes or an unreadable answer, and answer `Different` on a no; after the last, pass all the answers to `ReadVerdict`. Both ways reach the same verdict. The requests come in a fixed order, so you can build them again from the open question when its answer arrives. Set `MergeMembersEachWay = 0` for one request that compares the two groups as a whole, as 1.0 did.
- **`TryParse`** reads `{"same": true}` in the answer, also inside a code fence or after a model's reasoning, and a bare `yes`, `no`, `true` or `false`. **`TryParseChoice`** reads `{"group": n}` the same way, with `n` from 0 to the number of groups offered. `TryReadVerdict` uses the right one for the question and maps a group to its cluster (1 is the question's own cluster, then `OtherChoices` in order, 0 is none); anything it cannot read is `Failed`.
- **Temperature.** The settings carry a temperature of 0 only with `UseZeroTemperature` on (off by default, since current Claude models and OpenAI's reasoning models reject one).

## Things to know

- **Waiting is safe.** An item with an open question is in no cluster (`GetClusterOfAsync` returns null) until it is answered; other items keep being placed.
- **A failure is never a "no".** `Failed` keeps the question open. Only `Different` records refusals (one for each cluster offered), and refusals last: the item never joins a cluster that refused it, even after it merges into another.
- **Late answers are checked.** A merge answered `Same` is applied only if the two clusters are still within the merge-check zone; a join answered `Same` (or a pick) for a cluster that merged away goes to the survivor only if the item is still close to it.
- **One writer.** With `VectorDataClusterStore`, answer from the same process that adds items, or behind a lock: see [One writer](../concepts.md#one-writer).
