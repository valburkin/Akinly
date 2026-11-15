using System.Buffers.Binary;

namespace Akinly.VectorData.Records;

/// <summary>
/// A cluster's sum as text: its doubles, little-endian, in base64. About a third of the size of
/// JSON, and exact, so the centroid computed from it is the same on every machine.
/// </summary>
internal static class SumCodec
{
    public static string Encode(ReadOnlySpan<double> sum)
    {
        var bytes = new byte[sum.Length * sizeof(double)];
        for (var i = 0; i < sum.Length; i++)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(i * sizeof(double)), sum[i]);
        }

        return Convert.ToBase64String(bytes);
    }

    public static double[] Decode(string text)
    {
        var bytes = Convert.FromBase64String(text);
        if (bytes.Length % sizeof(double) != 0)
        {
            throw new InvalidDataException("A stored cluster's sum is not a whole number of doubles.");
        }

        var sum = new double[bytes.Length / sizeof(double)];
        for (var i = 0; i < sum.Length; i++)
        {
            sum[i] = BinaryPrimitives.ReadDoubleLittleEndian(bytes.AsSpan(i * sizeof(double)));
        }

        return sum;
    }
}
