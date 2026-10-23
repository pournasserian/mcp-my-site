namespace McpMySite.Core.Search;

/// <summary>
/// Merges keyword and vector rankings with reciprocal rank fusion, then collapses chunks to one hit per page.
/// Shared by every store so ranking behaves the same regardless of storage.
/// </summary>
public static class HybridRanker
{
    private const double RrfK = 60;

    public sealed record ChunkCandidate(int ChunkId, int PageId, int Ordinal, IReadOnlyList<string> HeadingPath, string Text);

    /// <summary>
    /// <paramref name="keyword"/> and <paramref name="vector"/> are ranked lists of chunk ids. Either may be empty.
    /// </summary>
    public static List<(int ChunkId, double Score)> Fuse(IReadOnlyList<int> keyword, IReadOnlyList<int> vector)
    {
        var scores = new Dictionary<int, double>();
        Add(keyword);
        Add(vector);
        return scores
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key)
            .Select(kv => (kv.Key, kv.Value))
            .ToList();

        void Add(IReadOnlyList<int> ranked)
        {
            for (var rank = 0; rank < ranked.Count; rank++)
            {
                scores[ranked[rank]] = scores.GetValueOrDefault(ranked[rank]) + 1.0 / (RrfK + rank + 1);
            }
        }
    }

    /// <summary>
    /// Keeps the best chunk per page, skips the first <see cref="SearchRequest.Offset"/> pages and builds the hit
    /// list, counting every distinct page in <paramref name="fused"/> for the total.
    /// </summary>
    public static SearchResponse ToPageHits(
        IReadOnlyList<(int ChunkId, double Score)> fused,
        Func<int, ChunkCandidate> chunkLookup,
        Func<int, PageDocument> pageLookup,
        SearchRequest request)
    {
        var hits = new List<SearchHit>();
        var seenPages = new HashSet<int>();
        foreach (var (chunkId, score) in fused)
        {
            var chunk = chunkLookup(chunkId);
            if (!seenPages.Add(chunk.PageId))
            {
                continue;
            }

            if (seenPages.Count <= request.Offset || hits.Count >= request.Limit)
            {
                continue;
            }

            var page = pageLookup(chunk.PageId);
            var (plain, highlighted) = Snippets.Build(chunk.Text, request.Query);
            hits.Add(new SearchHit
            {
                Url = page.Url,
                Title = page.Title,
                Description = page.Description,
                Score = Math.Round(score, 6),
                Snippet = plain,
                HighlightedSnippet = highlighted,
                HeadingPath = chunk.HeadingPath,
                ChunkId = chunkId,
            });
        }

        return new SearchResponse
        {
            Hits = hits,
            KeywordOnly = request.QueryVector is null,
            Total = seenPages.Count,
            HasMore = request.Offset + hits.Count < seenPages.Count,
        };
    }
}
