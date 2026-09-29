using System.Security.Cryptography;
using System.Text;

namespace GraphRag.Core;

public static class TextProcessing
{
    public static string Id(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..24];
    public static string EntityId(string name) => "entity-" + Id(name.Trim().Normalize(NormalizationForm.FormKC).ToUpperInvariant());

    public static string[] Chunk(string text, int size, int overlap)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (size < 1 || overlap < 0 || overlap >= size) throw new ArgumentOutOfRangeException(nameof(size));
        List<string> result = [];
        int start = 0;
        while (start < text.Length)
        {
            int end = Math.Min(start + size, text.Length);
            // Preserve surrogate pairs at chunk boundaries.
            if (end < text.Length && char.IsHighSurrogate(text[end - 1])) end--;
            result.Add(text[start..end]);
            if (end == text.Length) break;
            int next = Math.Max(start + 1, end - overlap);
            if (char.IsLowSurrogate(text[next])) next++;
            start = next;
        }
        return result.ToArray();
    }
}
