using Akinly.Storage;

namespace Akinly.Tests;

// The examples pass vectors inline, as the docs write them; a real caller passes embeddings from a model.
#pragma warning disable CA1861 // Prefer 'static readonly' fields over constant array arguments

/// <summary>
/// The README's examples that need only the core package, as written there (output goes to a
/// list instead of the console), so they keep compiling and doing what the README says.
/// </summary>
public sealed class ReadmeExampleTests
{
    [Fact]
    public async Task Your_own_embeddings_places_the_tickets_and_merges_the_two_heating_clusters()
    {
        var output = new List<string>();

        // From the README's "Your own embeddings" (also docs/getting-started.md).
        var clusterer = new Clusterer(new InMemoryClusterStore(), ClusteringOptions.CosineOnly("toy-3d", joinSimilarity: 0.85));

        var step = await clusterer.AddAsync(
        [
            new ClusterItem("ticket-1", new float[] { 1.00f, 0.00f, 0.00f }),   // "The heating is broken"
            new ClusterItem("ticket-2", new float[] { 0.82f, 0.57f, 0.00f }),   // "Heating and hot water are both out"
            new ClusterItem("ticket-3", new float[] { 0.97f, 0.26f, 0.00f }),   // "No heat in my apartment"
            new ClusterItem("ticket-4", new float[] { 0.00f, 0.00f, 1.00f }),   // "My rent went up 30 percent"
        ]);

        foreach (var placement in step.Placements)
        {
            output.Add($"{placement.ItemId} -> cluster {placement.ClusterId} ({placement.Reason})");
        }

        // ticket-3 pulled cluster 1 toward cluster 2: maintenance finds that they are now one.
        var maintained = await clusterer.MaintainAsync();
        foreach (var merge in maintained.Merges)
        {
            output.Add($"cluster {merge.FromId} merged into cluster {merge.IntoId}");
        }

        // What the README promises.
        Assert.Equal(
            [
                "ticket-1 -> cluster 1 (NewCluster)",
                "ticket-2 -> cluster 2 (NewCluster)",
                "ticket-3 -> cluster 1 (Similarity)",
                "ticket-4 -> cluster 3 (NewCluster)",
                "cluster 2 merged into cluster 1",
            ],
            output);
    }
}
