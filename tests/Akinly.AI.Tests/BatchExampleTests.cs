using System.Globalization;
using Akinly.Storage;
using Microsoft.Extensions.AI;

namespace Akinly.AI.Tests;

/// <summary>The batch loop of "Answer questions yourself", with a fake batch that answers every request.</summary>
public sealed class BatchExampleTests
{
    private static ClusterItem Item(string id, float x, float y) => new(id, new[] { x, y });

    [Fact]
    public async Task A_batch_answers_join_and_merge_questions_through_their_requests()
    {
        var texts = new InMemoryItemTextStore();
        foreach (var id in new[] { "a", "b", "c", "d" })
        {
            texts.Set(id, "text " + id);
        }

        // Two pairs: each pair joins on a question, then the two clusters are asked about as a merge.
        var clusterer = new Clusterer(new InMemoryClusterStore(), new ClusteringOptions
        {
            EmbeddingModel = "toy",
            JoinSimilarity = 0.99,
            CheckSimilarity = 0.9,
            MergeSimilarity = 0.99,
            MergeCheckSimilarity = 0.5,
            JoinChoices = 1,
        });
        await clusterer.AddAsync([Item("a", 1f, 0f), Item("c", 0.6f, 0.8f)]);
        await clusterer.AddAsync([Item("b", 0.98f, 0.199f), Item("d", 0.48f, 0.877f)]);

        var judgeOptions = new ChatClientJudgeOptions();
        var sent = new HashSet<QuestionId>();
        var requestCounts = new List<int>();
        for (var round = 0; round < 2; round++)
        {
            // 1. Build the requests, as in the guide.
            var requests = new List<(string CustomId, IList<ChatMessage> Messages, ChatOptions Settings)>();
            foreach (var question in await clusterer.GetOpenQuestionsAsync())
            {
                var built = await JudgePrompts.BuildRequestsAsync(question, texts, judgeOptions);
                var choice = question is JoinQuestion { OtherChoices.Count: > 0 };
                for (var part = 0; part < built!.Count; part++)
                {
                    requests.Add((string.Create(CultureInfo.InvariantCulture, $"{question.Id.Value}-{part}-{built.Count}"), built[part], JudgePrompts.CreateRequestOptions(judgeOptions, choice)));
                }

                sent.Add(question.Id);
            }

            requestCounts.Add(requests.Count);

            // 2 and 3. The batch says yes to everything; the answers come back in reverse order.
            var open = (await clusterer.GetOpenQuestionsAsync()).ToDictionary(q => q.Id);
            var answers = new Dictionary<QuestionId, (int RequestCount, List<string?> Replies)>();
            foreach (var (customId, text) in requests.Select(r => (r.CustomId, (string?)"""{"same": true}""")).Reverse())
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

            var verdicts = answers.Where(a => open.ContainsKey(a.Key)).Select(a => JudgePrompts.ReadVerdict(open[a.Key], a.Value.RequestCount, a.Value.Replies)).ToList();
            await clusterer.AnswerAsync(verdicts);
            await clusterer.MaintainAsync();
        }

        Assert.Equal([2, 2], requestCounts);   // two join questions, then one merge question asked both ways
        Assert.Empty(sent);
        Assert.Equal(await clusterer.GetClusterOfAsync("a"), await clusterer.GetClusterOfAsync("d"));
    }
}
