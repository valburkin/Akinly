namespace Akinly.Evaluation.Tests;

public sealed class EmbeddingsTests
{
    [Fact]
    public void A_cached_vector_round_trips_and_a_partial_one_is_ignored()
    {
        float[] vector = [0.25f, -1f, float.Epsilon];
        var bytes = Embeddings.Write(vector);

        Assert.Equal(vector, Embeddings.Read(bytes));
        Assert.Null(Embeddings.Read(bytes[..5]));
        Assert.Null(Embeddings.Read([]));
    }

    [Fact]
    public void The_same_model_on_another_server_has_its_own_cache()
    {
        var local = Embeddings.Folder("ollama:bge-m3", "http://localhost:11434/", "cache");

        Assert.NotEqual(local, Embeddings.Folder("ollama:bge-m3", "http://gpu-box:11434/", "cache"));
        Assert.Equal(local, Embeddings.Folder("ollama:bge-m3", "http://localhost:11434/", "cache"));
        Assert.StartsWith(Path.Combine("cache", "embeddings", "ollama_bge-m3-"), local, StringComparison.Ordinal);
    }
}
