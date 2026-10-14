namespace McpMySite.Server.Mcp;

/// <summary>
/// The two read-only tools the server exposes. Their shapes follow the search and fetch contract ChatGPT uses for
/// connectors and deep research (results carry an id, fetch takes it and returns text), and still accept a url.
/// </summary>
[McpServerToolType]
public sealed class SiteTools(SiteSearchService search)
{
    private const string Channel = "mcp";
    private const string SearchToolName = "search";
    private const int MaxDescriptionChars = 300;

    [McpServerTool(Name = SearchToolName, ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Search the site", UseStructuredContent = true)]
    [Description("Searches the website's pages with hybrid keyword and semantic search. Returns the best matching pages with a short snippet each. Call fetch with a result's id or URL to read the whole page.")]
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
                response.Hits.Select(h => new SearchToolHit(h.Url, h.Title, h.Url, h.Snippet, h.HeadingPath.Count == 0 ? null : string.Join(" > ", h.HeadingPath), h.Score)).ToList());
        }
        catch (IndexUnavailableException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    [McpServerTool(Name = "fetch", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Fetch a page", UseStructuredContent = true)]
    [Description("Returns the full content of one page of the website as Markdown in text, with its title, description, language and last modified date. Pass the id (or url) of a search result.")]
    public async Task<FetchToolResult> FetchAsync(
        [Description("The id of a search result.")] string? id = null,
        [Description("Absolute URL of the page. An alternative to id; pass either one.")] string? url = null,
        CancellationToken cancellationToken = default)
    {
        var target = (string.IsNullOrWhiteSpace(id) ? url : id)?.Trim();
        if (string.IsNullOrEmpty(target))
        {
            throw new McpException("Pass the id or url of a search result.");
        }

        try
        {
            var page = await search.FetchAsync(target, Channel, cancellationToken)
                ?? throw new McpException($"No indexed page at {target}. Use search to find valid ids.");
            return new FetchToolResult(page.Url, page.Url, page.Title, page.Markdown, page.Description, page.Language, page.Modified);
        }
        catch (IndexUnavailableException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    /// <summary>
    /// Puts the loaded site into the search tool's description. Clients read tool descriptions far more reliably
    /// than server instructions, so this is where a model learns which site the server covers.
    /// </summary>
    public static ListToolsResult DescribeSite(ListToolsResult result, IndexManifest? manifest)
    {
        if (manifest is null)
        {
            return result;
        }

        // The listed tools are shared definitions; copy the one that changes instead of editing it in place.
        result.Tools = result.Tools.Select(tool => tool.Name != SearchToolName ? tool : new Tool
        {
            Name = tool.Name,
            Title = tool.Title,
            Description = SearchDescription(manifest),
            InputSchema = tool.InputSchema,
            OutputSchema = tool.OutputSchema,
            Annotations = tool.Annotations,
            Icons = tool.Icons,
            Meta = tool.Meta,
        }).ToList();
        return result;
    }

    internal static string SearchDescription(IndexManifest manifest)
    {
        var name = manifest.SiteName ?? new Uri(manifest.SiteUrl).Host;
        var about = string.Join(' ', (manifest.SiteDescription ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (about.Length > MaxDescriptionChars)
        {
            about = about[..MaxDescriptionChars].TrimEnd() + "…";
        }

        var site = about.Length > 0 ? $"{name} ({manifest.SiteUrl}): {about}" : $"{name} ({manifest.SiteUrl}).";
        return $"Searches the website {site} "
            + $"Hybrid keyword and semantic search over its {manifest.PageCount} pages; returns the best matching pages with a short snippet each. "
            + "Call fetch with a result's id or URL to read the whole page.";
    }
}

public sealed record SearchToolResult(string Query, bool KeywordOnly, IReadOnlyList<SearchToolHit> Results);

/// <param name="Id">Identifier to pass to fetch. Currently the page URL.</param>
public sealed record SearchToolHit(string Id, string Title, string Url, string Snippet, string? Section, double Score);

/// <param name="Text">The whole page as Markdown.</param>
public sealed record FetchToolResult(string Id, string Url, string Title, string Text, string? Description, string Language, DateTimeOffset? Modified);
