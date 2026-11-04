namespace Akinly;

/// <summary>
/// Vector helpers for callers that keep clusters themselves, such as when two clusters merge.
/// Vectors are compared by direction only; the centroids the clusterers return are unit length.
/// </summary>
public static class Vectors
{
    private const double ZeroNorm = 1e-9;

    /// <summary>A unit-length copy of <paramref name="vector"/>; a zero vector comes back as a copy.</summary>
    public static float[] Normalize(float[] vector)
    {
        ArgumentNullException.ThrowIfNull(vector);
        var norm = Math.Sqrt(Dot(vector, vector));
        if (norm < ZeroNorm)
        {
            return (float[])vector.Clone();
        }

        var copy = new float[vector.Length];
        for (var i = 0; i < vector.Length; i++)
        {
            copy[i] = (float)(vector[i] / norm);
        }

        return copy;
    }

    /// <summary>The dot product: the cosine similarity of two unit vectors.</summary>
    public static double Dot(float[] a, float[] b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var sum = 0.0;
        for (var i = 0; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }

    /// <summary>The normalized mean of unit vectors; empty for no vectors.</summary>
    public static float[] Mean(IReadOnlyList<float[]> vectors)
    {
        ArgumentNullException.ThrowIfNull(vectors);
        if (vectors.Count == 0)
        {
            return [];
        }

        var mean = new float[vectors[0].Length];
        foreach (var v in vectors)
        {
            for (var i = 0; i < mean.Length; i++)
            {
                mean[i] += v[i];
            }
        }

        return Normalize(mean);
    }

    /// <summary>
    /// The normalized mean of two centroids weighted by their member counts: the centroid of
    /// two clusters merged into one.
    /// </summary>
    public static float[] WeightedMean(float[] a, int countA, float[] b, int countB)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var mean = new float[a.Length];
        for (var i = 0; i < mean.Length; i++)
        {
            mean[i] = (a[i] * countA) + (b[i] * countB);
        }

        return Normalize(mean);
    }

    /// <summary>
    /// Folds one more unit-length member into a running-mean centroid that now has
    /// <paramref name="newCount"/> members, in place, and renormalizes it.
    /// </summary>
    public static void AddToMean(float[] centroid, float[] sample, int newCount)
    {
        ArgumentNullException.ThrowIfNull(centroid);
        ArgumentNullException.ThrowIfNull(sample);
        for (var i = 0; i < centroid.Length; i++)
        {
            centroid[i] = ((centroid[i] * (newCount - 1)) + sample[i]) / newCount;
        }

        // Renormalize, so cosine similarity stays a dot product on the unit sphere.
        var norm = Math.Sqrt(Dot(centroid, centroid));
        if (norm < ZeroNorm)
        {
            return;
        }

        for (var i = 0; i < centroid.Length; i++)
        {
            centroid[i] = (float)(centroid[i] / norm);
        }
    }
}
