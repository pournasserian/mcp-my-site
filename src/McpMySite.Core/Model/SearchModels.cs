namespace McpMySite.Core.Model;

public sealed record SearchRequest
{
    // Fixed, not grown from this request's own Offset and Limit: a pool sized for the current page used to pull
    // in more distinct pages on a later page, so Total (the pages found in the pool) rose as the reader paged
    // forward instead of describing the query. Sized for the API's largest supported page (SearchApi:MaxOffset
    // 100 plus SearchApi:MaxLimit 20) so paging that far still has enough candidates to fill the page.
    private const int CandidatePoolSize = (100 + 20) * 5;

    public required string Query { get; init; }

    /// <summary>Embedding of <see cref="Query"/>. When null the store runs keyword search only.</summary>
    public ReadOnlyMemory<float>? QueryVector { get; init; }

    /// <summary>Maximum number of pages to return.</summary>
    public int Limit { get; init; } = 10;

    /// <summary>Pages to skip before the first hit, for paging.</summary>
    public int Offset { get; init; }

    /// <summary>
    /// How many chunks each ranker contributes before merging. Fixed per query so every page of one query is cut
    /// from the same ranking and <see cref="SearchResponse.Total"/> does not depend on <see cref="Offset"/>.
    /// </summary>
    public int CandidateCount => CandidatePoolSize;
}

public sealed record SearchHit
{
    public required string Url { get; init; }

    public required string Title { get; init; }

    public string? Description { get; init; }

    public required double Score { get; init; }

    /// <summary>Plain-text excerpt of the best matching chunk.</summary>
    public required string Snippet { get; init; }

    /// <summary>Same excerpt, HTML-escaped, with matched terms wrapped in &lt;mark&gt;.</summary>
    public required string HighlightedSnippet { get; init; }

    public IReadOnlyList<string> HeadingPath { get; init; } = [];

    public int ChunkId { get; init; }
}

public sealed record SearchResponse
{
    public required IReadOnlyList<SearchHit> Hits { get; init; }

    /// <summary>True when the vector half of the hybrid search did not run.</summary>
    public required bool KeywordOnly { get; init; }

    /// <summary>Distinct pages that matched within the candidate pool, including those before and after this page of hits.</summary>
    public required int Total { get; init; }

    /// <summary>True when a larger offset would return further hits.</summary>
    public required bool HasMore { get; init; }
}
