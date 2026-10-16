namespace McpMySite.Server.Api;

/// <summary>HTTP search endpoint for the site's own search box. Same search service as the MCP tool.</summary>
public static class SearchEndpoints
{
    public const string CorsPolicy = "search-api";
    public const string RateLimitPolicy = "per-ip";

    public static IEndpointConventionBuilder MapSearchApi(this IEndpointRouteBuilder app, SearchApiOptions options)
    {
        var endpoint = app.MapGet(options.Path, async (
            [FromQuery(Name = "q"), Description("What to look for. Natural language or keywords.")] string? query,
            [FromQuery, Description("Maximum number of results. Defaults to SearchApi:DefaultLimit and is capped at SearchApi:MaxLimit.")] int? limit,
            SiteSearchService search,
            IOptions<SearchApiOptions> apiOptions,
            CancellationToken cancellationToken) =>
        {
            var settings = apiOptions.Value;
            if (string.IsNullOrWhiteSpace(query))
            {
                return Results.BadRequest(new ApiError("Query parameter q is required."));
            }

            if (query.Length > settings.MaxQueryLength)
            {
                return Results.BadRequest(new ApiError($"Query is longer than {settings.MaxQueryLength} characters."));
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
                return Results.Json(new ApiError(ex.Message), statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        })
        .WithName("Search")
        .WithTags("Search")
        .WithSummary("Search the site")
        .WithDescription("Returns the pages that best match the query, ranked by hybrid keyword and semantic search, each with a plain and a highlighted snippet. "
            + "When the index holds no embeddings, or the query cannot be embedded, the results come from keyword search alone and keywordOnly is true.")
        .Produces<SearchApiResponse>()
        .Produces<ApiError>(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status429TooManyRequests)
        .Produces<ApiError>(StatusCodes.Status503ServiceUnavailable)
        // q is bound as optional so the handler can answer a missing query with its own error body; the contract still requires it.
        .AddOpenApiOperationTransformer((operation, _, _) =>
        {
            if (operation.Parameters?.FirstOrDefault(p => p.Name == "q") is OpenApiParameter q)
            {
                q.Required = true;
            }

            return Task.CompletedTask;
        })
        .RequireRateLimiting(RateLimitPolicy)
        .RequireCors(CorsPolicy);

        return endpoint;
    }
}

/// <summary>Body of every error the HTTP API returns.</summary>
public sealed record ApiError(string Error);

public sealed record SearchApiResponse(string Query, bool KeywordOnly, IReadOnlyList<SearchApiResult> Results);

public sealed record SearchApiResult(
    string Title,
    string Url,
    string? Description,
    string Snippet,
    string HighlightedSnippet,
    IReadOnlyList<string> Section,
    double Score);
