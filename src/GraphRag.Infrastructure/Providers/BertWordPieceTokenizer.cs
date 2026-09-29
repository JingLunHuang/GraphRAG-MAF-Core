using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace GraphRag.Infrastructure.Providers;

// BERT uncased WordPiece contract, suitable for all-MiniLM-L6-v2 exports with vocab.txt.
// Other tokenizers require their matching implementation; never silently substitute tokenization.
public sealed partial class BertWordPieceTokenizer
{
    private readonly Dictionary<string, long> _vocabulary;
    private readonly int _maxTokens;
    [GeneratedRegex(@"[\p{IsCJKUnifiedIdeographs}]|[\p{L}\p{N}]+|[^\s\p{L}\p{N}]", RegexOptions.CultureInvariant)]
    private static partial Regex BasicTokens();
    public BertWordPieceTokenizer(string vocabularyPath, int maxTokens = 256)
    {
        _vocabulary = File.ReadLines(vocabularyPath).Select((text, i) => (text, i)).ToDictionary(x => x.text, x => (long)x.i, StringComparer.Ordinal);
        _maxTokens = maxTokens;
        if (maxTokens < 2 || new[] { "[CLS]", "[SEP]", "[UNK]" }.Any(token => !_vocabulary.ContainsKey(token)))
            throw new ArgumentException("Invalid BERT WordPiece vocabulary or max token count.");
    }

    public long[] Encode(string text)
    {
        StringBuilder normalized = new();
        foreach (char c in text.ToLowerInvariant().Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && !char.IsControl(c)) normalized.Append(c);
        List<long> ids = [_vocabulary["[CLS]"]];
        foreach (Match token in BasicTokens().Matches(normalized.ToString()))
        {
            List<long> word = [];
            int start = 0;
            while (start < token.Length)
            {
                int end = token.Length; long? match = null;
                while (end > start)
                {
                    string piece = (start == 0 ? "" : "##") + token.Value[start..end];
                    if (_vocabulary.TryGetValue(piece, out long value)) { match = value; break; }
                    end--;
                }
                if (match is null) { word = [_vocabulary["[UNK]"]]; break; }
                word.Add(match.Value); start = end;
            }
            foreach (long value in word)
            {
                if (ids.Count >= _maxTokens - 1) break;
                ids.Add(value);
            }
            if (ids.Count >= _maxTokens - 1) break;
        }
        ids.Add(_vocabulary["[SEP]"]);
        return ids.ToArray();
    }
}
