namespace McpMySite.Server.Api;

/// <summary>
/// GET /api/info: what this server serves and how fresh the index is. The landing page renders the same model,
/// so everything a person sees there is also available to scripts and monitors.
/// </summary>
public static class InfoEndpoints
{
    public const string Path = "/api/info";

    public static IEndpointConventionBuilder MapInfoApi(this IEndpointRouteBuilder app) =>
        app.MapGet(Path, (HttpRequest request, IndexHolder holder, IOptions<McpOptions> mcp, IOptions<SearchApiOptions> searchApi, IOptions<UiOptions> ui) =>
            {
                var info = SiteInfo.Create(holder, mcp.Value, searchApi.Value, ui.Value, SiteInfo.BaseUrl(request, ui.Value), DateTimeOffset.UtcNow);
                return info.Index is null ? Results.Json(info, statusCode: StatusCodes.Status503ServiceUnavailable) : Results.Ok(info);
            })
            .WithName("Info")
            .WithTags("Info")
            .WithSummary("Describe the server and its index")
            .WithDescription("Returns the site, the loaded index with its build statistics, and the endpoint URLs. Answers 503 with the same shape, and no index, until an index is loaded.")
            .Produces<SiteInfo>()
            .Produces<SiteInfo>(StatusCodes.Status503ServiceUnavailable)
            .RequireCors(SearchEndpoints.CorsPolicy)
            .AllowHead();
}

/// <param name="Status">"ok", or "no-index" until an index is loaded.</param>
public sealed record SiteInfo(string Status, SiteSummary? Site, IndexSummary? Index, EndpointUrls Endpoints, ServerSummary Server)
{
    public static SiteInfo Create(IndexHolder holder, McpOptions mcp, SearchApiOptions searchApi, UiOptions ui, string baseUrl, DateTimeOffset now)
    {
        var endpoints = new EndpointUrls(
            baseUrl + mcp.Path,
            !string.IsNullOrWhiteSpace(mcp.ApiKey),
            searchApi.Enabled ? baseUrl + searchApi.Path : null,
            baseUrl + OpenApiSetup.DocumentPath,
            ui.Enabled ? baseUrl + OpenApiSetup.ReferencePath + "/" : null);
        var server = new ServerSummary(mcp.ServerName, typeof(SiteInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");

        var manifest = holder.Current?.Manifest;
        if (manifest is null)
        {
            return new SiteInfo("no-index", null, null, endpoints, server);
        }

        var language = manifest.Stats?.Languages.Where(l => l.Key != "und").OrderByDescending(l => l.Value).Select(l => l.Key).FirstOrDefault();
        var site = new SiteSummary(manifest.SiteName ?? new Uri(manifest.SiteUrl).Host, manifest.SiteUrl, manifest.SiteDescription, language);
        var stale = ui.StaleAfterDays > 0 && now - manifest.BuiltAt > TimeSpan.FromDays(ui.StaleAfterDays);
        var index = new IndexSummary(
            manifest.BuiltAt, holder.LoadedAt, stale, manifest.PageCount, manifest.ChunkCount, manifest.StoreType, manifest.FormatVersion,
            manifest.KeywordOnly, manifest.EmbeddingModel, manifest.EmbeddingDimensions, manifest.IndexerVersion, manifest.Stats);
        return new SiteInfo("ok", site, index, endpoints, server);
    }

    /// <summary>Ui:PublicUrl when set, otherwise the scheme, host and path base of the request. No trailing slash.</summary>
    public static string BaseUrl(HttpRequest request, UiOptions ui) =>
        (string.IsNullOrWhiteSpace(ui.PublicUrl) ? $"{request.Scheme}://{request.Host}{request.PathBase}" : ui.PublicUrl).TrimEnd('/');
}

/// <param name="Language">Main content language, when the index records it.</param>
public sealed record SiteSummary(string Name, string Url, string? Description, string? Language);

/// <param name="Stale">True when the index is older than Ui:StaleAfterDays.</param>
/// <param name="KeywordOnly">True when the index holds no embeddings and every search is keyword search.</param>
/// <param name="EmbeddingModel">Absent for a keyword-only index.</param>
/// <param name="Stats">Numbers from the indexer run; absent for indexes from older indexers.</param>
public sealed record IndexSummary(
    DateTimeOffset BuiltAt,
    DateTimeOffset? LoadedAt,
    bool Stale,
    int PageCount,
    int ChunkCount,
    string Store,
    int FormatVersion,
    bool KeywordOnly,
    string? EmbeddingModel,
    int EmbeddingDimensions,
    string? IndexerVersion,
    IndexStats? Stats);

/// <param name="McpRequiresKey">True when MCP requests must carry an API key.</param>
/// <param name="SearchApi">Absent when the search API is turned off.</param>
/// <param name="Docs">Absent when the pages are turned off.</param>
public sealed record EndpointUrls(string Mcp, bool McpRequiresKey, string? SearchApi, string OpenApi, string? Docs);

public sealed record ServerSummary(string Name, string Version);
