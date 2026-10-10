namespace McpMySite.Indexer.Sources;

/// <summary>A page as delivered by a source, before extraction.</summary>
public sealed record RawPage(string Url, string Html, DateTimeOffset? LastModifiedHint);

public sealed record SourceError(string Url, string Message);

public interface ISiteSource
{
    /// <summary>Yields pages as they are read. Errors are collected in <see cref="Errors"/>.</summary>
    IAsyncEnumerable<RawPage> GetPagesAsync(CancellationToken cancellationToken);

    IReadOnlyList<SourceError> Errors { get; }
}
