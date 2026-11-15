namespace Akinly;

/// <summary>
/// Vector helpers for callers that keep clusters themselves, such as when two clusters merge.
/// Vectors are compared by direction only; the centroids the clusterer returns are unit length.
/// Arithmetic runs in double precision, so very large or very small components neither
/// overflow nor vanish.
/// </summary>
public static class Vectors
{
    private const double ZeroNorm = 1e-9;

    /// <summary>A unit-length copy of a vector.</summary>
    /// <param name="vector">The vector, of any length.</param>
    /// <returns>A new array; a vector shorter than 1e-9 counts as zero and comes back as a zero copy.</returns>
    public static float[] Normalize(float[] vector)
    {
        ArgumentNullException.ThrowIfNull(vector);
        var norm = Math.Sqrt(Dot(vector, vector));
        var copy = new float[vector.Length];
        if (norm < ZeroNorm)
        {
            return copy;
        }

        for (var i = 0; i < vector.Length; i++)
        {
            copy[i] = (float)(vector[i] / norm);
        }

        return copy;
    }

    /// <summary>The dot product of two vectors: the cosine similarity of two unit vectors.</summary>
    /// <param name="a">The first vector.</param>
    /// <param name="b">The second vector, of the same length.</param>
    /// <returns>The dot product; NaN when either vector holds a NaN.</returns>
    /// <exception cref="ArgumentException">The vectors have different lengths.</exception>
    public static double Dot(float[] a, float[] b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        Guard.SameLength(a, b, nameof(b));
        var sum = 0.0;
        for (var i = 0; i < a.Length; i++)
        {
            sum += (double)a[i] * b[i];
        }

        return sum;
    }

    /// <summary>The normalized mean of unit vectors.</summary>
    /// <param name="vectors">The vectors, all of one length.</param>
    /// <returns>A new unit vector; empty for no vectors.</returns>
    /// <exception cref="ArgumentException">The vectors have different lengths, or one is null.</exception>
    public static float[] Mean(IReadOnlyList<float[]> vectors)
    {
        ArgumentNullException.ThrowIfNull(vectors);
        if (vectors.Count == 0)
        {
            return [];
        }

        var dimensions = -1;
        var sum = new double[Guard.Dimensions(vectors[0], -1, "Vector 0", nameof(vectors))];
        for (var v = 0; v < vectors.Count; v++)
        {
            dimensions = Guard.Dimensions(vectors[v], dimensions, $"Vector {v}", nameof(vectors));
            for (var i = 0; i < sum.Length; i++)
            {
                sum[i] += vectors[v][i];
            }
        }

        return NormalizeSum(sum);
    }

    /// <summary>
    /// The normalized mean of two centroids weighted by their member counts: the centroid of
    /// two clusters merged into one.
    /// </summary>
    /// <param name="a">The first cluster's centroid.</param>
    /// <param name="countA">The first cluster's member count, zero or more.</param>
    /// <param name="b">The second cluster's centroid, of the same length.</param>
    /// <param name="countB">The second cluster's member count, zero or more.</param>
    /// <returns>A new unit vector.</returns>
    public static float[] WeightedMean(float[] a, int countA, float[] b, int countB)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        Guard.SameLength(a, b, nameof(b));
        ArgumentOutOfRangeException.ThrowIfNegative(countA);
        ArgumentOutOfRangeException.ThrowIfNegative(countB);
        var sum = new double[a.Length];
        for (var i = 0; i < sum.Length; i++)
        {
            sum[i] = ((double)a[i] * countA) + ((double)b[i] * countB);
        }

        return NormalizeSum(sum);
    }

    /// <summary>
    /// Folds one more unit-length member into a running-mean centroid, in place, and
    /// renormalizes it. Renormalizing at each step reweights earlier members slightly, so the
    /// result approximates the mean of all members: close in direction, not equal.
    /// </summary>
    /// <param name="centroid">The centroid to update, unit length (or zero, for a cluster with no members).</param>
    /// <param name="sample">The new member, unit length, of the same length.</param>
    /// <param name="newCount">The member count including the new member: 1 or more.</param>
    public static void AddToMean(float[] centroid, float[] sample, int newCount)
    {
        ArgumentNullException.ThrowIfNull(centroid);
        ArgumentNullException.ThrowIfNull(sample);
        Guard.SameLength(centroid, sample, nameof(sample));
        ArgumentOutOfRangeException.ThrowIfLessThan(newCount, 1);
        for (var i = 0; i < centroid.Length; i++)
        {
            centroid[i] = (float)((((double)centroid[i] * (newCount - 1)) + sample[i]) / newCount);
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

    private static float[] NormalizeSum(double[] sum)
    {
        var norm = 0.0;
        foreach (var x in sum)
        {
            norm += x * x;
        }

        norm = Math.Sqrt(norm);
        var result = new float[sum.Length];
        if (norm < ZeroNorm)
        {
            return result;
        }

        for (var i = 0; i < sum.Length; i++)
        {
            result[i] = (float)(sum[i] / norm);
        }

        return result;
    }
}
