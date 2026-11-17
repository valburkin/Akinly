using System.Numerics.Tensors;

namespace Akinly.Engine;

/// <summary>Vector arithmetic for the engine: sums in double precision, comparisons with SIMD.</summary>
internal static class VectorMath
{
    private const double ZeroNorm = 1e-9;

    /// <summary>The cosine similarity of two unit vectors.</summary>
    public static double Similarity(ReadOnlyMemory<float> a, ReadOnlyMemory<float> b) =>
        TensorPrimitives.Dot(a.Span, b.Span);

    /// <summary>
    /// The average cosine similarity of a member of one cluster to a member of the other, from
    /// the sums of their unit vectors: the dot product of the sums over the product of the counts.
    /// An empty cluster is similar to nothing (0).
    /// </summary>
    public static double AverageSimilarity(ReadOnlySpan<double> sum, int count, ReadOnlySpan<double> otherSum, int otherCount) =>
        count == 0 || otherCount == 0 ? 0 : TensorPrimitives.Dot(sum, otherSum) / ((double)count * otherCount);

    /// <summary>A unit-length copy of a vector, or null when it is zero or not finite.</summary>
    public static float[]? UnitOrNull(ReadOnlySpan<float> vector)
    {
        var norm = 0.0;
        foreach (var x in vector)
        {
            if (!float.IsFinite(x))
            {
                return null;
            }

            norm += (double)x * x;
        }

        norm = Math.Sqrt(norm);
        if (norm < ZeroNorm || !double.IsFinite(norm))
        {
            return null;
        }

        var unit = new float[vector.Length];
        for (var i = 0; i < unit.Length; i++)
        {
            unit[i] = (float)(vector[i] / norm);
        }

        return unit;
    }

    /// <summary>A sum plus (<paramref name="sign"/> 1) or minus (-1) a vector, as a new array.</summary>
    public static double[] Add(ReadOnlySpan<double> sum, ReadOnlySpan<float> vector, int sign = 1)
    {
        var result = new double[vector.Length];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = (i < sum.Length ? sum[i] : 0) + (sign * (double)vector[i]);
        }

        return result;
    }

    /// <summary>The sum of two sums, as a new array.</summary>
    public static double[] Add(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        var result = new double[Math.Max(a.Length, b.Length)];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = (i < a.Length ? a[i] : 0) + (i < b.Length ? b[i] : 0);
        }

        return result;
    }

    /// <summary>The sum of vectors, in double precision.</summary>
    public static double[] Sum(IEnumerable<ReadOnlyMemory<float>> vectors, int dimensions)
    {
        var sum = new double[dimensions];
        foreach (var vector in vectors)
        {
            var span = vector.Span;
            for (var i = 0; i < sum.Length; i++)
            {
                sum[i] += span[i];
            }
        }

        return sum;
    }

    /// <summary>The unit direction of a sum, or <paramref name="fallback"/> when the sum is (nearly) zero.</summary>
    public static ReadOnlyMemory<float> Direction(ReadOnlySpan<double> sum, ReadOnlyMemory<float> fallback)
    {
        var norm = 0.0;
        foreach (var x in sum)
        {
            norm += x * x;
        }

        norm = Math.Sqrt(norm);
        if (norm < ZeroNorm)
        {
            return fallback;
        }

        var direction = new float[sum.Length];
        for (var i = 0; i < direction.Length; i++)
        {
            direction[i] = (float)(sum[i] / norm);
        }

        return direction;
    }
}
