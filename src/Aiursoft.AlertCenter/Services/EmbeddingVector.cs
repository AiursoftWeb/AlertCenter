using System.Buffers.Binary;

namespace Aiursoft.AlertCenter.Services;

internal static class EmbeddingVector
{
    public static float[] Normalize(float[] vector)
    {
        if (vector.Length is 0 or > 65536 || vector.Any(value => !float.IsFinite(value)))
            throw new InvalidDataException("The embedding server returned an invalid vector.");
        var norm = Math.Sqrt(vector.Sum(value => (double)value * value));
        if (norm == 0) throw new InvalidDataException("The embedding server returned a zero vector.");
        return vector.Select(value => (float)(value / norm)).ToArray();
    }

    public static byte[] Serialize(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        for (var index = 0; index < vector.Length; index++)
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(index * sizeof(float)), vector[index]);
        return bytes;
    }

    public static float[]? Deserialize(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length % sizeof(float) != 0 || bytes.Length > 65536 * sizeof(float)) return null;
        var vector = new float[bytes.Length / sizeof(float)];
        for (var index = 0; index < vector.Length; index++)
        {
            vector[index] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(index * sizeof(float)));
            if (!float.IsFinite(vector[index])) return null;
        }
        return vector.Any(value => value != 0) ? vector : null;
    }

    public static double Similarity(float[] left, float[] right)
    {
        double score = 0;
        for (var index = 0; index < left.Length; index++) score += (double)left[index] * right[index];
        return score;
    }
}
