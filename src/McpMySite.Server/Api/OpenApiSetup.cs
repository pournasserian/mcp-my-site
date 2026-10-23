using Microsoft.AspNetCore.OpenApi;
using Scalar.AspNetCore;

namespace McpMySite.Server.Api;

/// <summary>OpenAPI document for the HTTP API and the Scalar reference page that renders it.</summary>
public static class OpenApiSetup
{
    public const string DocumentPath = "/openapi/v1.json";
    public const string ReferencePath = "/docs";

    public static IServiceCollection AddSiteOpenApi(this IServiceCollection services) =>
        services.AddOpenApi(options =>
        {
            // The API routes answer HEAD for link checkers and monitors; the document describes the GET operations only.
            options.ShouldInclude = description =>
                (description.GroupName is null || description.GroupName == options.DocumentName) && !HttpMethods.IsHead(description.HttpMethod ?? "");

            options.AddDocumentTransformer((document, context, _) =>
            {
                // The document is generated per request, so the title follows the index that is loaded now.
                var siteName = SiteName(context.ApplicationServices.GetRequiredService<IndexHolder>());
                document.Info = new OpenApiInfo
                {
                    Title = $"{siteName} search API",
                    Version = "v1",
                    Description = $"Hybrid keyword and semantic search over the pages of {siteName}, and details of the index behind it. "
                        + "The same index is served to AI agents through the MCP endpoint, which is not part of this document.",
                };
                return Task.CompletedTask;
            });
        });

    public static void MapSiteOpenApi(this WebApplication app, UiOptions ui)
    {
        app.MapOpenApi(DocumentPath).AllowHead();
        if (!ui.ServesApiReference)
        {
            return;
        }

        app.MapScalarApiReference(ReferencePath, (options, context) =>
        {
            options.Title = $"{SiteName(context.RequestServices.GetRequiredService<IndexHolder>())} search API";
            options.OpenApiRoutePattern = DocumentPath;
        }).AllowHead();
    }

    private static string SiteName(IndexHolder holder) =>
        holder.Current?.Manifest is { } manifest ? manifest.SiteName ?? manifest.SiteUrl : "mcp-my-site";
}
