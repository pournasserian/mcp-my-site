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
    /// <summary>Which optional parts the pages render. Decided here with the rest, so the Ui switches live in one place; not part of the JSON.</summary>
    [JsonIgnore]
    public VisibleParts Visible { get; init; } = VisibleParts.All;

    public static SiteInfo Create(IndexHolder holder, McpOptions mcp, SearchApiOptions searchApi, UiOptions ui, string baseUrl, DateTimeOffset now)
    {
        // What is hidden here is hidden everywhere the model is rendered: the page, /api/info, llms.txt and the JSON-LD.
        var endpoints = new EndpointUrls(
            baseUrl + mcp.Path,
            !string.IsNullOrWhiteSpace(mcp.ApiKey),
            searchApi.Enabled && ui.ShowSearchApi ? baseUrl + searchApi.Path : null,
            ui.ShowSearchApi ? baseUrl + OpenApiSetup.DocumentPath : null,
            ui.ServesApiReference ? baseUrl + OpenApiSetup.ReferencePath + "/" : null);
        var visible = new VisibleParts(ui.ShowSearchDetails, ui.ShowBuildDetails);
        var server = new ServerSummary(mcp.ServerName, typeof(SiteInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");

        var manifest = holder.Current?.Manifest;
        if (manifest is null)
        {
            return new SiteInfo("no-index", null, null, endpoints, server) { Visible = visible };
        }

        var language = manifest.Stats?.Languages.Where(l => l.Key != "und").OrderByDescending(l => l.Value).Select(l => l.Key).FirstOrDefault();
        var site = new SiteSummary(manifest.SiteName ?? new Uri(manifest.SiteUrl).Host, manifest.SiteUrl, manifest.SiteDescription, language);
        var stale = ui.StaleAfterDays > 0 && now - manifest.BuiltAt > TimeSpan.FromDays(ui.StaleAfterDays);
        var index = new IndexSummary(
            manifest.BuiltAt, holder.LoadedAt, stale, manifest.PageCount, manifest.ChunkCount,
            ui.ShowBuildDetails ? manifest.StoreType : null,
            ui.ShowBuildDetails ? manifest.FormatVersion : null,
            ui.ShowSearchDetails ? manifest.KeywordOnly : null,
            ui.ShowSearchDetails ? manifest.EmbeddingModel : null,
            ui.ShowSearchDetails ? manifest.EmbeddingDimensions : null,
            ui.ShowBuildDetails ? manifest.IndexerVersion : null,
            IndexStatsSummary.From(manifest.Stats, ui.ShowBuildDetails, ui.ShowSearchDetails));
        return new SiteInfo("ok", site, index, endpoints, server) { Visible = visible };
    }

    /// <summary>Ui:PublicUrl when set, otherwise the scheme, host and path base of the request. No trailing slash.</summary>
    public static string BaseUrl(HttpRequest request, UiOptions ui) =>
        (string.IsNullOrWhiteSpace(ui.PublicUrl) ? $"{request.Scheme}://{request.Host}{request.PathBase}" : ui.PublicUrl).TrimEnd('/');
}

/// <param name="Language">Main content language, when the index records it.</param>
public sealed record SiteSummary(string Name, string Url, string? Description, string? Language);

/// <param name="Stale">True when the index is older than Ui:StaleAfterDays.</param>
/// <param name="Store">Absent when Ui:ShowBuildDetails is off, like FormatVersion and IndexerVersion.</param>
/// <param name="KeywordOnly">True when the index holds no embeddings and every search is keyword search. Absent when Ui:ShowSearchDetails is off, like the embedding model and dimensions.</param>
/// <param name="EmbeddingModel">Absent for a keyword-only index.</param>
/// <param name="Stats">Numbers from the indexer run; absent for indexes from older indexers.</param>
public sealed record IndexSummary(
    DateTimeOffset BuiltAt,
    DateTimeOffset? LoadedAt,
    bool Stale,
    int PageCount,
    int ChunkCount,
    string? Store,
    int? FormatVersion,
    bool? KeywordOnly,
    string? EmbeddingModel,
    int? EmbeddingDimensions,
    string? IndexerVersion,
    IndexStatsSummary? Stats);

/// <summary>IndexStats as served: the content numbers always, the build numbers only while Ui:ShowBuildDetails is on.</summary>
/// <param name="EmbeddingsGenerated">Absent when either Ui:ShowBuildDetails or Ui:ShowSearchDetails is off, like EmbeddingsFromCache.</param>
public sealed record IndexStatsSummary(
    string? Source,
    DateTimeOffset? StartedAt,
    double? DurationSeconds,
    int? PagesFetched,
    int? PagesSkippedNoIndex,
    int? PagesSkippedDuplicate,
    int? PagesSkippedEmpty,
    int? Errors,
    int? EmbeddingsGenerated,
    int? EmbeddingsFromCache,
    long Words,
    DateTimeOffset? NewestPageModified,
    IReadOnlyDictionary<string, int> Languages,
    IReadOnlyList<SectionStats> Sections)
{
    public static IndexStatsSummary? From(IndexStats? stats, bool showBuild, bool showSearch) => stats is null ? null : new(
        showBuild ? stats.Source : null,
        showBuild ? stats.StartedAt : null,
        showBuild ? stats.DurationSeconds : null,
        showBuild ? stats.PagesFetched : null,
        showBuild ? stats.PagesSkippedNoIndex : null,
        showBuild ? stats.PagesSkippedDuplicate : null,
        showBuild ? stats.PagesSkippedEmpty : null,
        showBuild ? stats.Errors : null,
        showBuild && showSearch ? stats.EmbeddingsGenerated : null,
        showBuild && showSearch ? stats.EmbeddingsFromCache : null,
        stats.Words,
        stats.NewestPageModified,
        stats.Languages,
        stats.Sections);
}

/// <summary>The optional parts of the landing page, from the Ui switches.</summary>
public sealed record VisibleParts(bool SearchDetails, bool BuildDetails)
{
    public static readonly VisibleParts All = new(true, true);
}

/// <param name="McpRequiresKey">True when MCP requests must carry an API key.</param>
/// <param name="SearchApi">Absent when the search API is turned off or Ui:ShowSearchApi is off.</param>
/// <param name="OpenApi">Absent when Ui:ShowSearchApi is off; the document is still served.</param>
/// <param name="Docs">Absent when the pages or Ui:ApiReference are turned off.</param>
public sealed record EndpointUrls(string Mcp, bool McpRequiresKey, string? SearchApi, string? OpenApi, string? Docs);

public sealed record ServerSummary(string Name, string Version);
