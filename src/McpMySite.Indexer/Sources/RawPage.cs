using McpMySite.Indexer.Extraction;

namespace McpMySite.Indexer.Sources;

/// <summary>
/// A page as delivered by a source, before extraction. When <see cref="Replayed"/> is set the page was not
/// downloaded: it is the extraction stored by a previous build, and <see cref="Html"/> is empty.
/// </summary>
public sealed record RawPage(string Url, string Html, DateTimeOffset? LastModifiedHint)
{
    public string? ETag { get; init; }

    public string? LastModified { get; init; }

    public DateTimeOffset? SitemapLastmod { get; init; }

    public IReadOnlyList<string> Links { get; init; } = [];

    public ExtractedPage? Replayed { get; init; }
}

public sealed record SourceError(string Url, string Message);

public interface ISiteSource
{
    /// <summary>Yields pages as they are read. Errors are collected in <see cref="Errors"/>.</summary>
    IAsyncEnumerable<RawPage> GetPagesAsync(CancellationToken cancellationToken);

    IReadOnlyList<SourceError> Errors { get; }
}
