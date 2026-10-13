using McpMySite.Server.Search;
using Microsoft.AspNetCore.Mvc;

namespace McpMySite.Server.Api;

/// <summary>HTTP search endpoint for the site's own search box. Same search service as the MCP tool.</summary>
public static class SearchEndpoints
{
    public const string CorsPolicy = "search-api";
    public const string RateLimitPolicy = "per-ip";

    public static IEndpointConventionBuilder MapSearchApi(this IEndpointRouteBuilder app, SearchApiOptions options)
    {
        var endpoint = app.MapGet(options.Path, async (
            [FromQuery(Name = "q")] string? query,
            [FromQuery] int? limit,
            SiteSearchService search,
            IOptions<SearchApiOptions> apiOptions,
            CancellationToken cancellationToken) =>
        {
            var settings = apiOptions.Value;
            if (string.IsNullOrWhiteSpace(query))
            {
                return Results.BadRequest(new { error = "Query parameter q is required." });
            }

            if (query.Length > settings.MaxQueryLength)
            {
                return Results.BadRequest(new { error = $"Query is longer than {settings.MaxQueryLength} characters." });
            }

            var effectiveLimit = Math.Clamp(limit ?? settings.DefaultLimit, 1, settings.MaxLimit);
            try
            {
                var response = await search.SearchAsync(query.Trim(), effectiveLimit, "api", cancellationToken);
                return Results.Ok(new SearchApiResponse(
                    query,
                    response.KeywordOnly,
                    response.Hits.Select(h => new SearchApiResult(h.Title, h.Url, h.Description, h.Snippet, h.HighlightedSnippet, h.HeadingPath, h.Score)).ToList()));
            }
            catch (IndexUnavailableException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        })
        .WithName("Search")
        .RequireRateLimiting(RateLimitPolicy)
        .RequireCors(CorsPolicy);

        return endpoint;
    }
}

public sealed record SearchApiResponse(string Query, bool KeywordOnly, IReadOnlyList<SearchApiResult> Results);

public sealed record SearchApiResult(
    string Title,
    string Url,
    string? Description,
    string Snippet,
    string HighlightedSnippet,
    IReadOnlyList<string> Section,
    double Score);
