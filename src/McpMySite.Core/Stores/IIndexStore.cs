namespace McpMySite.Core.Stores;

/// <summary>
/// A loaded, read-only index. Each store runs keyword search, vector search and merging itself so the
/// web app does not depend on how the data is laid out.
/// </summary>
public interface IIndexStore : IDisposable
{
    IndexManifest Manifest { get; }

    Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default);

    /// <summary>Returns the page at <paramref name="url"/>, tolerant of trailing slashes and scheme differences.</summary>
    Task<PageDocument?> GetPageAsync(string url, CancellationToken cancellationToken = default);

    /// <summary>URL and title of every page, for outlines and diagnostics.</summary>
    IReadOnlyList<(string Url, string Title)> ListPages();
}
