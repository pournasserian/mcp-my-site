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
    /// Keeps the best chunk per page, skips the first <see cref="SearchRequest.Offset"/> pages and picks the chunks
    /// that become the page of hits, counting every distinct page in <paramref name="fused"/> for the total. Only
    /// needs each candidate's page, so a store can load chunk text for the selected few alone.
    /// </summary>
    public static (List<(int ChunkId, double Score)> Selected, int Total) SelectPage(
        IReadOnlyList<(int ChunkId, double Score)> fused,
        Func<int, int> pageIdLookup,
        SearchRequest request)
    {
        var selected = new List<(int ChunkId, double Score)>();
        var seenPages = new HashSet<int>();
        foreach (var candidate in fused)
        {
            if (!seenPages.Add(pageIdLookup(candidate.ChunkId)))
            {
                continue;
            }

            if (seenPages.Count > request.Offset && selected.Count < request.Limit)
            {
                selected.Add(candidate);
            }
        }

        return (selected, seenPages.Count);
    }

    /// <summary>Builds the response for the chunks <see cref="SelectPage"/> picked, with snippets from their text.</summary>
    public static SearchResponse ToResponse(
        IReadOnlyList<(int ChunkId, double Score)> selected,
        int total,
        Func<int, ChunkCandidate> chunkLookup,
        Func<int, PageDocument> pageLookup,
        SearchRequest request)
    {
        var hits = new List<SearchHit>(selected.Count);
        foreach (var (chunkId, score) in selected)
        {
            var chunk = chunkLookup(chunkId);
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
            Total = total,
            HasMore = request.Offset + hits.Count < total,
        };
    }

    /// <summary><see cref="SelectPage"/> and <see cref="ToResponse"/> in one step, for a store with every chunk in memory.</summary>
    public static SearchResponse ToPageHits(
        IReadOnlyList<(int ChunkId, double Score)> fused,
        Func<int, ChunkCandidate> chunkLookup,
        Func<int, PageDocument> pageLookup,
        SearchRequest request)
    {
        var (selected, total) = SelectPage(fused, id => chunkLookup(id).PageId, request);
        return ToResponse(selected, total, chunkLookup, pageLookup, request);
    }
}
