using System.Numerics.Tensors;

namespace GraphRag.Core;

public sealed class VectorIndex
{
    private TextChunk[] _chunks = [];
    public int Count => Volatile.Read(ref _chunks).Length;
    public TextChunk[] Snapshot => Volatile.Read(ref _chunks);

    public void Replace(TextChunk[] chunks)
    {
        if (chunks.Length != 0)
        {
            int dimensions = chunks[0].Vector.Length;
            foreach (TextChunk chunk in chunks)
            {
                Validate(chunk.Vector);
                if (chunk.Vector.Length != dimensions) throw new InvalidOperationException("Embedding dimensions changed; rebuild the index.");
            }
        }
        Volatile.Write(ref _chunks, chunks);
    }

    public void Add(TextChunk[] chunks)
    {
        TextChunk[] current = Snapshot;
        Replace(current.Concat(chunks).DistinctBy(c => c.Id).ToArray());
    }

    public Evidence[] Search(ReadOnlySpan<float> query, int topK)
    {
        Validate(query);
        if (topK is < 1 or > 50) throw new ArgumentOutOfRangeException(nameof(topK));
        List<Evidence> candidates = [];
        foreach (TextChunk chunk in Snapshot)
        {
            float score = Cosine(query, chunk.Vector);
            candidates.Add(new Evidence(chunk.Id, chunk.Source, chunk.Text, score));
        }
        return candidates.OrderByDescending(e => e.Score).ThenBy(e => e.Id, StringComparer.Ordinal).Take(topK).ToArray();
    }

    // The numerical kernel allocates zero managed bytes; top-k results allocate by design.
    public static float Cosine(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        if (left.Length == 0 || left.Length != right.Length) throw new ArgumentException("Nonempty vectors of equal dimensions are required.");
        float result = TensorPrimitives.CosineSimilarity(left, right);
        if (!float.IsFinite(result)) throw new ArgumentException("Cosine similarity requires finite, nonzero vectors.");
        return Math.Clamp(result, -1, 1);
    }

    public static void Validate(ReadOnlySpan<float> vector)
    {
        if (vector.Length == 0) throw new ArgumentException("Embedding cannot be empty.");
        double norm = 0;
        foreach (float value in vector)
        {
            if (!float.IsFinite(value)) throw new ArgumentException("Embedding must contain finite values.");
            norm += (double)value * value;
        }
        if (norm == 0) throw new ArgumentException("Embedding cannot be zero.");
    }
}
