namespace McpMySite.Core.Model;

public sealed record SearchRequest
{
    public required string Query { get; init; }

    /// <summary>Embedding of <see cref="Query"/>. When null the store runs keyword search only.</summary>
    public ReadOnlyMemory<float>? QueryVector { get; init; }

    /// <summary>Maximum number of pages to return.</summary>
    public int Limit { get; init; } = 10;

    /// <summary>How many chunks each ranker contributes before merging.</summary>
    public int CandidateCount { get; init; } = 50;
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
}
