using Akinly.Storage;
using static Akinly.Tests.TestVectors;

namespace Akinly.Tests;

/// <summary>Labels and the size a cluster had when labeled.</summary>
public sealed class LabelTests
{
    [Fact]
    public async Task Setting_a_label_records_the_size_and_clearing_it_resets_it()
    {
        var clusterer = new Clusterer(new InMemoryClusterStore(), CosineOnly());
        await clusterer.AddAsync([Item("a", At(0)), Item("b", At(1))]);

        await clusterer.SetLabelAsync(1, "Heating");
        Assert.Equal(2, (await clusterer.GetClusterAsync(1))!.LabeledAtCount);

        await clusterer.SetLabelAsync(1, null);
        var cleared = await clusterer.GetClusterAsync(1);
        Assert.Equal((null, 0), (cleared!.Label, cleared.LabeledAtCount));
    }

    [Fact]
    public async Task A_merge_keeps_the_survivors_label_or_takes_the_other_ones()
    {
        var clusterer = new Clusterer(new InMemoryClusterStore(), CosineOnly(o => o.MergeSimilarity = o.MergeCheckSimilarity = 0.8));
        await clusterer.AddAsync([Item("a", At(0)), Item("a2", At(1)), Item("b", At(30))]);
        await clusterer.SetLabelAsync(2, "From the smaller one");

        await clusterer.MaintainAsync();

        var survivor = await clusterer.GetClusterAsync(1);
        Assert.Equal(("From the smaller one", 1), (survivor!.Label, survivor.LabeledAtCount));
    }

    [Fact]
    public async Task Labels_survive_an_export_and_import()
    {
        var store = new InMemoryClusterStore();
        var clusterer = new Clusterer(store, CosineOnly());
        await clusterer.AddAsync([Item("a", At(0)), Item("b", At(1)), Item("c", At(2))]);
        await clusterer.SetLabelAsync(1, "Heating");

        using var stream = new MemoryStream();
        await store.ExportAsync(stream);
        stream.Position = 0;
        var restored = await new Clusterer(await InMemoryClusterStore.ImportAsync(stream), CosineOnly()).GetClusterAsync(1);

        Assert.Equal(("Heating", 3), (restored!.Label, restored.LabeledAtCount));
    }
}
