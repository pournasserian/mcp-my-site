namespace McpMySite.Core.Search;

/// <summary>In-memory BM25 over a fixed set of documents. Built once per index load.</summary>
public sealed class Bm25Index
{
    private const double K1 = 1.2;
    private const double B = 0.75;

    private readonly Dictionary<string, List<(int DocIndex, int TermFrequency)>> _postings = new();
    private readonly int[] _docLengths;
    private readonly double _averageLength;

    public Bm25Index(IReadOnlyList<string> documents)
    {
        _docLengths = new int[documents.Count];
        long total = 0;
        for (var i = 0; i < documents.Count; i++)
        {
            var tokens = Tokenizer.Tokenize(documents[i]);
            _docLengths[i] = tokens.Count;
            total += tokens.Count;

            var counts = new Dictionary<string, int>();
            foreach (var token in tokens)
            {
                counts[token] = counts.GetValueOrDefault(token) + 1;
            }

            foreach (var (term, tf) in counts)
            {
                if (!_postings.TryGetValue(term, out var list))
                {
                    _postings[term] = list = new List<(int, int)>();
                }

                list.Add((i, tf));
            }
        }

        _averageLength = documents.Count == 0 ? 1 : (double)total / documents.Count;
    }

    public int DocumentCount => _docLengths.Length;

    /// <summary>Returns the top <paramref name="limit"/> documents ordered by score descending.</summary>
    public List<(int DocIndex, double Score)> Search(string query, int limit)
    {
        var terms = Tokenizer.Tokenize(query).Distinct().ToList();
        if (terms.Count == 0 || DocumentCount == 0)
        {
            return [];
        }

        var scores = new Dictionary<int, double>();
        var n = DocumentCount;
        foreach (var term in terms)
        {
            if (!_postings.TryGetValue(term, out var postings))
            {
                continue;
            }

            var df = postings.Count;
            var idf = Math.Log(1 + (n - df + 0.5) / (df + 0.5));
            foreach (var (doc, tf) in postings)
            {
                var norm = K1 * (1 - B + B * _docLengths[doc] / _averageLength);
                var score = idf * (tf * (K1 + 1)) / (tf + norm);
                scores[doc] = scores.GetValueOrDefault(doc) + score;
            }
        }

        return scores
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key)
            .Take(limit)
            .Select(kv => (kv.Key, kv.Value))
            .ToList();
    }
}
