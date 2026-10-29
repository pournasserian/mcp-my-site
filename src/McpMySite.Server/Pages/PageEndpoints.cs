using Microsoft.AspNetCore.Http.HttpResults;
using System.Text;
using System.Xml.Linq;

namespace McpMySite.Server.Pages;

/// <summary>The landing page and the files search engines look for: robots.txt and sitemap.xml.</summary>
public static class PageEndpoints
{
    private static readonly XNamespace SitemapNamespace = "http://www.sitemaps.org/schemas/sitemap/0.9";

    public static void MapPages(this WebApplication app, UiOptions ui, MaintainersOptions maintainers)
    {
        // robots.txt is served either way, so a server without pages tells crawlers to stay out entirely.
        app.MapGet("/robots.txt", (HttpRequest request, IOptions<UiOptions> options, IOptions<McpOptions> mcp) =>
            Results.Text(RobotsTxt(SiteInfo.BaseUrl(request, options.Value), options.Value.Enabled, mcp.Value.Path), "text/plain; charset=utf-8"))
            .AllowHead()
            .ExcludeFromDescription();

        // Independent of Ui:Enabled, so a server with the pages off can still be claimed on a directory. Mapped
        // only when maintainers are configured, so an unconfigured server keeps answering 404 as before.
        if (maintainers.Count > 0)
        {
            app.MapGet("/.well-known/mcpbundles.json", () => Results.Json(McpBundlesJson(maintainers)))
                .AllowHead()
                .ExcludeFromDescription();

            app.MapGet("/.well-known/security.txt", (HttpRequest request, IOptions<UiOptions> options, TimeProvider time) =>
                Results.Text(SecurityTxt(SiteInfo.BaseUrl(request, options.Value), maintainers, time.GetUtcNow()), "text/plain; charset=utf-8"))
                .AllowHead()
                .ExcludeFromDescription();
        }

        if (!ui.Enabled)
        {
            return;
        }

        if (ui.LandingPage)
        {
            MapLandingPage(app);
        }

        app.MapGet("/llms.txt", async (HttpRequest request, IndexHolder holder, LlmsTxt llms, IOptions<McpOptions> mcp, IOptions<SearchApiOptions> searchApi, IOptions<UiOptions> options, CancellationToken cancellationToken) =>
        {
            var baseUrl = SiteInfo.BaseUrl(request, options.Value);
            var info = SiteInfo.Create(holder, mcp.Value, searchApi.Value, options.Value, baseUrl, DateTimeOffset.UtcNow);
            var text = await llms.GetAsync(info, baseUrl, cancellationToken);
            return text is null
                ? Results.Text("No index is loaded yet.\n", "text/plain; charset=utf-8", statusCode: StatusCodes.Status503ServiceUnavailable)
                : Results.Text(text, "text/plain; charset=utf-8");
        }).AllowHead().ExcludeFromDescription();

        app.MapGet("/sitemap.xml", (HttpRequest request, IndexHolder holder, IOptions<UiOptions> options) =>
            Results.Text(Sitemap(SiteInfo.BaseUrl(request, options.Value), holder.Current?.Manifest.BuiltAt, options.Value), "application/xml; charset=utf-8"))
            .AllowHead()
            .ExcludeFromDescription();
    }

    private static void MapLandingPage(WebApplication app)
    {
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
        }).AllowHead().ExcludeFromDescription();

        // The playground was removed in 0.3.0; old links land on the landing page's test section until a later release.
        app.MapGet("/playground", () => Results.Redirect("/#test")).AllowHead().ExcludeFromDescription();
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

    internal static string Sitemap(string baseUrl, DateTimeOffset? builtAt, UiOptions ui)
    {
        var lastModified = builtAt?.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var pages = new List<string>();
        if (ui.LandingPage)
        {
            pages.Add(baseUrl + "/");
        }

        if (ui.ApiReference)
        {
            pages.Add(baseUrl + OpenApiSetup.ReferencePath + "/");
        }

        var urls = pages.Select(url => new XElement(SitemapNamespace + "url",
            new XElement(SitemapNamespace + "loc", url),
            lastModified is null ? null : new XElement(SitemapNamespace + "lastmod", lastModified)));
        var document = new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement(SitemapNamespace + "urlset", urls));

        // XDocument.ToString leaves the declaration out.
        return document.Declaration + "\n" + document.Root;
    }

    internal static MaintainersDocument McpBundlesJson(IReadOnlyList<MaintainerOptions> maintainers) =>
        new([.. maintainers.Select(m => new MaintainerContact(m.Email, m.Name))]);

    internal static string SecurityTxt(string baseUrl, IReadOnlyList<MaintainerOptions> maintainers, DateTimeOffset now)
    {
        var builder = new StringBuilder();
        foreach (var maintainer in maintainers)
        {
            builder.Append($"Contact: mailto:{maintainer.Email}\n");
        }

        // RFC 9116 requires Expires; a year out is far enough to not need rotation on every deployment.
        builder.Append($"Expires: {now.AddYears(1).UtcDateTime:yyyy-MM-dd'T'HH:mm:ss'Z'}\n");
        builder.Append("Preferred-Languages: en\n");
        builder.Append($"Canonical: {baseUrl}/.well-known/security.txt\n");
        return builder.ToString();
    }
}

/// <summary>The body of /.well-known/mcpbundles.json.</summary>
public sealed record MaintainersDocument(IReadOnlyList<MaintainerContact> Maintainers);

/// <param name="Name">Omitted from the JSON when not set.</param>
public sealed record MaintainerContact(string Email, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name);
