namespace McpMySite.Server.Mcp;

/// <summary>The two read-only tools the server exposes.</summary>
[McpServerToolType]
public sealed class SiteTools(SiteSearchService search)
{
    private const string Channel = "mcp";

    [McpServerTool(Name = "search", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Search the site", UseStructuredContent = true)]
    [Description("Searches the website's pages with hybrid keyword and semantic search. Returns the best matching pages with a short snippet each. Call fetch with a result URL to read the whole page.")]
    public async Task<SearchToolResult> SearchAsync(
        [Description("What to look for. Natural language or keywords.")] string query,
        [Description("Maximum number of pages to return, 1 to 20. Default 10.")] int limit = 10,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new McpException("query must not be empty.");
        }

        try
        {
            var response = await search.SearchAsync(query.Trim(), Math.Clamp(limit, 1, 20), Channel, cancellationToken);
            return new SearchToolResult(
                query,
                response.KeywordOnly,
                response.Hits.Select(h => new SearchToolHit(h.Title, h.Url, h.Snippet, h.HeadingPath.Count == 0 ? null : string.Join(" > ", h.HeadingPath), h.Score)).ToList());
        }
        catch (IndexUnavailableException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    [McpServerTool(Name = "fetch", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Fetch a page", UseStructuredContent = true)]
    [Description("Returns the full content of one page of the website as Markdown, with its title, description, language and last modified date. Use a URL returned by search.")]
    public async Task<FetchToolResult> FetchAsync(
        [Description("Absolute URL of the page, as returned by search.")] string url,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new McpException("url must not be empty.");
        }

        try
        {
            var page = await search.FetchAsync(url.Trim(), Channel, cancellationToken)
                ?? throw new McpException($"No indexed page at {url}. Use search to find valid URLs.");
            return new FetchToolResult(page.Url, page.Title, page.Description, page.Language, page.Modified, page.Markdown);
        }
        catch (IndexUnavailableException ex)
        {
            throw new McpException(ex.Message);
        }
    }
}

public sealed record SearchToolResult(string Query, bool KeywordOnly, IReadOnlyList<SearchToolHit> Results);

public sealed record SearchToolHit(string Title, string Url, string Snippet, string? Section, double Score);

public sealed record FetchToolResult(string Url, string Title, string? Description, string Language, DateTimeOffset? Modified, string Markdown);
