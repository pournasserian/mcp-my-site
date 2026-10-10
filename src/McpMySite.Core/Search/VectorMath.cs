using System.Numerics;
using System.Runtime.InteropServices;

namespace McpMySite.Core.Search;

public static class VectorMath
{
    /// <summary>Scales <paramref name="vector"/> to unit length in place. Zero vectors are left as is.</summary>
    public static void NormalizeInPlace(Span<float> vector)
    {
        var norm = MathF.Sqrt(Dot(vector, vector));
        if (norm <= float.Epsilon)
        {
            return;
        }

        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] /= norm;
        }
    }

    public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
        {
            throw new ArgumentException($"Vector lengths differ: {a.Length} vs {b.Length}.");
        }

        var width = Vector<float>.Count;
        var acc = Vector<float>.Zero;
        var i = 0;
        for (; i <= a.Length - width; i += width)
        {
            acc += new Vector<float>(a.Slice(i, width)) * new Vector<float>(b.Slice(i, width));
        }

        var sum = Vector.Dot(acc, Vector<float>.One);
        for (; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }

    /// <summary>
    /// Brute-force nearest neighbours over a flat array of unit vectors laid out row by row.
    /// Returns the top <paramref name="limit"/> rows by cosine similarity, descending.
    /// </summary>
    public static List<(int Row, double Score)> TopK(ReadOnlySpan<float> vectors, int dimensions, ReadOnlySpan<float> query, int limit)
    {
        if (dimensions <= 0 || vectors.Length == 0 || limit <= 0)
        {
            return [];
        }

        var rows = vectors.Length / dimensions;
        var q = query.ToArray();
        NormalizeInPlace(q);

        var scores = new (int Row, double Score)[rows];
        for (var r = 0; r < rows; r++)
        {
            scores[r] = (r, Dot(vectors.Slice(r * dimensions, dimensions), q));
        }

        return scores
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Row)
            .Take(limit)
            .ToList();
    }

    public static byte[] ToBytes(ReadOnlySpan<float> vector) => MemoryMarshal.AsBytes(vector).ToArray();

    public static float[] FromBytes(ReadOnlySpan<byte> bytes) => MemoryMarshal.Cast<byte, float>(bytes).ToArray();
}
