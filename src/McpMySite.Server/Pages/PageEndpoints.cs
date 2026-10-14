using Microsoft.AspNetCore.Http.HttpResults;
using System.Text;
using System.Xml.Linq;

namespace McpMySite.Server.Pages;

/// <summary>The landing page, the MCP playground and the files search engines look for: robots.txt and sitemap.xml.</summary>
public static class PageEndpoints
{
    public const string PlaygroundPath = "/playground";

    private static readonly XNamespace SitemapNamespace = "http://www.sitemaps.org/schemas/sitemap/0.9";

    public static void MapPages(this WebApplication app, UiOptions ui)
    {
        // robots.txt is served either way, so a server without pages tells crawlers to stay out entirely.
        app.MapGet("/robots.txt", (HttpRequest request, IOptions<UiOptions> options, IOptions<McpOptions> mcp) =>
            Results.Text(RobotsTxt(SiteInfo.BaseUrl(request, options.Value), options.Value.Enabled, mcp.Value.Path), "text/plain; charset=utf-8"))
            .ExcludeFromDescription();

        if (!ui.Enabled)
        {
            return;
        }

        app.MapGet("/", (HttpRequest request, IndexHolder holder, IOptions<McpOptions> mcp, IOptions<SearchApiOptions> searchApi, IOptions<UiOptions> options) =>
        {
            var baseUrl = SiteInfo.BaseUrl(request, options.Value);
            var now = DateTimeOffset.UtcNow;
            var info = SiteInfo.Create(holder, mcp.Value, searchApi.Value, options.Value, baseUrl, now);
            return new RazorComponentResult<LandingPage>(new { Info = info, BaseUrl = baseUrl, Now = now })
            {
                // Crawlers retry a 503 later instead of indexing an empty page.
                StatusCode = info.Index is null ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status200OK,
            };
        }).ExcludeFromDescription();

        app.MapGet(PlaygroundPath, (HttpRequest request, IndexHolder holder, IOptions<McpOptions> mcp, IOptions<SearchApiOptions> searchApi, IOptions<UiOptions> options) =>
        {
            var baseUrl = SiteInfo.BaseUrl(request, options.Value);
            var info = SiteInfo.Create(holder, mcp.Value, searchApi.Value, options.Value, baseUrl, DateTimeOffset.UtcNow);
            return new RazorComponentResult<PlaygroundPage>(new { Info = info, BaseUrl = baseUrl, EndpointPath = request.PathBase + mcp.Value.Path });
        }).ExcludeFromDescription();

        app.MapGet("/llms.txt", async (HttpRequest request, IndexHolder holder, LlmsTxt llms, IOptions<McpOptions> mcp, IOptions<SearchApiOptions> searchApi, IOptions<UiOptions> options, CancellationToken cancellationToken) =>
        {
            var baseUrl = SiteInfo.BaseUrl(request, options.Value);
            var info = SiteInfo.Create(holder, mcp.Value, searchApi.Value, options.Value, baseUrl, DateTimeOffset.UtcNow);
            var text = await llms.GetAsync(info, baseUrl, cancellationToken);
            return text is null
                ? Results.Text("No index is loaded yet.\n", "text/plain; charset=utf-8", statusCode: StatusCodes.Status503ServiceUnavailable)
                : Results.Text(text, "text/plain; charset=utf-8");
        }).ExcludeFromDescription();

        app.MapGet("/sitemap.xml", (HttpRequest request, IndexHolder holder, IOptions<UiOptions> options) =>
            Results.Text(Sitemap(SiteInfo.BaseUrl(request, options.Value), holder.Current?.Manifest.BuiltAt), "application/xml; charset=utf-8"))
            .ExcludeFromDescription();
    }

    internal static string RobotsTxt(string baseUrl, bool pagesEnabled, string mcpPath)
    {
        if (!pagesEnabled)
        {
            return "User-agent: *\nDisallow: /\n";
        }

        // The MCP endpoint and the JSON API answer machines, not readers.
        return new StringBuilder()
            .Append("User-agent: *\n")
            .Append($"Disallow: {mcpPath}\n")
            .Append("Disallow: /api/\n")
            .Append($"\nSitemap: {baseUrl}/sitemap.xml\n")
            .ToString();
    }

    internal static string Sitemap(string baseUrl, DateTimeOffset? builtAt)
    {
        var lastModified = builtAt?.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var urls = new[] { baseUrl + "/", baseUrl + PlaygroundPath, baseUrl + OpenApiSetup.ReferencePath + "/" }.Select(url => new XElement(SitemapNamespace + "url",
            new XElement(SitemapNamespace + "loc", url),
            lastModified is null ? null : new XElement(SitemapNamespace + "lastmod", lastModified)));
        var document = new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement(SitemapNamespace + "urlset", urls));

        // XDocument.ToString leaves the declaration out.
        return document.Declaration + "\n" + document.Root;
    }
}
