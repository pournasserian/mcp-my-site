using Microsoft.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace McpMySite.Server.Mcp;

/// <summary>
/// The server card (SEP-2127) at {Mcp:Path}/server-card: the identity of this server and how to reach it, built from the
/// running configuration and the loaded index. Tools, resources and capabilities stay out on purpose; the SEP leaves
/// them to tools/list and initialize, which answer for the running server.
/// </summary>
public static class ServerCard
{
    public const string PathSuffix = "/server-card";
    public const string MediaType = "application/mcp-server-card+json";
    public const string CorsPolicy = "server-card";
    public const string SchemaUrl = "https://static.modelcontextprotocol.io/schemas/v1/server-card.schema.json";

    // The software's identity: the registry name in server.json and the image label. Deployments differ only in URL and site.
    public const string Name = "io.github.pournasserian/mcp-my-site";
    public const string RepositoryUrl = "https://github.com/pournasserian/mcp-my-site";

    // The SDK keeps its list internal (McpProtocolVersions.SupportedProtocolVersions); a test checks the two agree.
    public static readonly string[] SupportedProtocolVersions = ["2024-11-05", "2025-03-26", "2025-06-18", "2025-11-25", "2026-07-28"];

    private const int MaxDescriptionChars = 100;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Path(string mcpPath) => mcpPath.TrimEnd('/') + PathSuffix;

    public static IEndpointConventionBuilder MapServerCard(this IEndpointRouteBuilder app, McpOptions mcp) =>
        app.MapGet(Path(mcp.Path), (HttpRequest request, HttpResponse response, IndexHolder holder, IOptions<McpOptions> mcpOptions, IOptions<UiOptions> ui) =>
        {
            var card = Build(holder.Current?.Manifest, mcpOptions.Value, SiteInfo.BaseUrl(request, ui.Value));
            var body = JsonSerializer.SerializeToUtf8Bytes(card, JsonOptions);
            var etag = new EntityTagHeaderValue($"\"{Convert.ToHexStringLower(SHA256.HashData(body))[..16]}\"");

            var headers = response.GetTypedHeaders();
            headers.CacheControl = new CacheControlHeaderValue { Public = true, MaxAge = TimeSpan.FromHours(1) };
            headers.ETag = etag;

            var ifNoneMatch = request.GetTypedHeaders().IfNoneMatch;
            if (ifNoneMatch.Any(tag => tag.Equals(EntityTagHeaderValue.Any) || tag.Compare(etag, useStrongComparison: false)))
            {
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }

            return Results.Bytes(body, MediaType);
        })
        .RequireRateLimiting(SearchEndpoints.RateLimitPolicy)
        .RequireCors(CorsPolicy)
        .AllowHead()
        .ExcludeFromDescription();

    internal static ServerCardDocument Build(IndexManifest? manifest, McpOptions mcp, string baseUrl)
    {
        var siteName = manifest?.SiteName ?? (manifest is null ? null : new Uri(manifest.SiteUrl).Host);
        var description = manifest is null
            ? "Search and fetch tools for one website, served from an index built with mcpmysite."
            : $"Search and fetch tools for the pages of {new Uri(manifest.SiteUrl).Host}.";
        if (description.Length > MaxDescriptionChars)
        {
            description = description[..(MaxDescriptionChars - 1)] + "…";
        }

        var headers = string.IsNullOrWhiteSpace(mcp.ApiKey)
            ? null
            : new[] { new ServerCardHeader("Authorization", "Bearer <key>: the API key the server's operator hands out. X-Api-Key: <key> works too.", true, true) };

        return new ServerCardDocument(
            SchemaUrl,
            Name,
            typeof(ServerCard).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            siteName is null ? "mcp-my-site" : $"{siteName} (mcp-my-site)",
            description,
            new ServerCardRepository(RepositoryUrl, "github"),
            RepositoryUrl + "#readme",
            [new ServerCardRemote("streamable-http", baseUrl + mcp.Path, SupportedProtocolVersions, headers)]);
    }
}

public sealed record ServerCardDocument(
    [property: JsonPropertyName("$schema")] string Schema,
    string Name,
    string Version,
    string Title,
    string Description,
    ServerCardRepository Repository,
    string WebsiteUrl,
    IReadOnlyList<ServerCardRemote> Remotes);

public sealed record ServerCardRepository(string Url, string Source);

public sealed record ServerCardRemote(string Type, string Url, IReadOnlyList<string> SupportedProtocolVersions, IReadOnlyList<ServerCardHeader>? Headers);

public sealed record ServerCardHeader(string Name, string Description, bool IsRequired, bool IsSecret);
