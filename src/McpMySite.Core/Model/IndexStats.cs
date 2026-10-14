namespace McpMySite.Core.Model;

/// <summary>
/// Numbers from the indexer run that produced an index, kept in the manifest so a server can show them
/// without the run report. Optional: indexes built before it existed have none.
/// </summary>
public sealed record IndexStats
{
    /// <summary>"crawl" or "folder".</summary>
    public string? Source { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>Time from the start of the run until the index was written.</summary>
    public double DurationSeconds { get; init; }

    public int PagesFetched { get; init; }

    public int PagesSkippedNoIndex { get; init; }

    public int PagesSkippedDuplicate { get; init; }

    public int PagesSkippedEmpty { get; init; }

    /// <summary>Pages that could not be fetched or extracted.</summary>
    public int Errors { get; init; }

    public int EmbeddingsGenerated { get; init; }

    public int EmbeddingsFromCache { get; init; }

    /// <summary>Approximate number of words across all indexed pages.</summary>
    public long Words { get; init; }

    /// <summary>Most recent last-modified date among pages that declare one.</summary>
    public DateTimeOffset? NewestPageModified { get; init; }

    /// <summary>Pages per BCP-47 language tag ("und" when unknown).</summary>
    public IReadOnlyDictionary<string, int> Languages { get; init; } = new Dictionary<string, int>();

    /// <summary>Pages per top-level path, largest first.</summary>
    public IReadOnlyList<SectionStats> Sections { get; init; } = [];
}

/// <param name="Path">First path segment, such as "/docs", or "/" for the home page.</param>
/// <param name="Title">Title of the section's own landing page, when it was indexed.</param>
/// <param name="Pages">Indexed pages in the section.</param>
public sealed record SectionStats(string Path, string? Title, int Pages);
