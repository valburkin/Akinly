namespace Akinly;

/// <summary>Vector helpers shared by the clusterers. Vectors are compared by direction only.</summary>
internal static class VectorMath
{
    private const double ZeroNorm = 1e-9;

    /// <summary>A unit-length copy of <paramref name="vector"/>; a zero vector comes back as is.</summary>
    public static float[] Normalize(float[] vector)
    {
        var norm = Math.Sqrt(Dot(vector, vector));
        if (norm < ZeroNorm)
        {
            return vector;
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
        var sum = 0.0;
        for (var i = 0; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }

    /// <summary>
    /// Folds one more unit-length member into a running-mean centroid of
    /// <paramref name="newCount"/> members, in place, and renormalizes it.
    /// </summary>
    public static void AddToMean(float[] centroid, float[] sample, int newCount)
    {
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
