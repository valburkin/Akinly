namespace Akinly.Tests;

/// <summary>Small vectors for tests: directions in a low-dimensional space with known similarities.</summary>
internal static class TestVectors
{
    public const string Model = "test-embedding";

    /// <summary>A unit vector at <paramref name="degrees"/> in the first plane of a 4-dimensional space.</summary>
    public static float[] At(double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return [(float)Math.Cos(radians), (float)Math.Sin(radians), 0f, 0f];
    }

    /// <summary>A unit vector along one of the 4 axes.</summary>
    public static float[] Axis(int axis)
    {
        var v = new float[4];
        v[axis] = 1;
        return v;
    }

    /// <summary>The angle whose cosine is <paramref name="similarity"/>, in degrees.</summary>
    public static double AngleFor(double similarity) => Math.Acos(similarity) * 180 / Math.PI;

    public static ClusterItem Item(string id, float[] embedding) => new(id, embedding);

    /// <summary>Options with an ask zone: join at 0.9, ask from 0.6; merge at 0.95, ask from 0.8.</summary>
    public static ClusteringOptions Judged(Action<ClusteringOptions>? adjust = null)
    {
        var options = new ClusteringOptions
        {
            EmbeddingModel = Model,
            JoinSimilarity = 0.9,
            CheckSimilarity = 0.6,
            MergeSimilarity = 0.95,
            MergeCheckSimilarity = 0.8,
            FailedAnswers = new FailedAnswerOptions { MaxAttempts = 3, RetryDelay = TimeSpan.Zero },

            // Yes-or-no questions, one cluster each, so a test can follow every step; questions
            // with choices have their own tests.
            JoinChoices = 1,
        };
        adjust?.Invoke(options);
        return options;
    }

    /// <summary>Cosine-only options: join and merge at 0.9.</summary>
    public static ClusteringOptions CosineOnly(Action<ClusteringOptions>? adjust = null)
    {
        var options = ClusteringOptions.CosineOnly(Model, 0.9);
        adjust?.Invoke(options);
        return options;
    }
}
