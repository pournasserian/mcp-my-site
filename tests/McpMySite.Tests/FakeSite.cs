using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using System.Collections.Concurrent;
using System.Text;

namespace McpMySite.Tests;

/// <summary>
/// A small site for crawl tests: robots.txt, an optional sitemap and HTML pages that carry ETag and Last-Modified
/// and answer 304 to a matching conditional request. Every request is recorded with its conditional headers.
/// </summary>
public sealed class FakeSite : IAsyncDisposable
{
    public sealed record Page(string Html, string? ETag = null, string? LastModified = null, DateTimeOffset? Lastmod = null);

    public sealed record Request(string Path, string? IfNoneMatch, string? IfModifiedSince);

    private readonly WebApplication _app;
    private readonly ConcurrentQueue<Request> _requests = new();

    public ConcurrentDictionary<string, Page> Pages { get; } = new();

    public bool Sitemap { get; set; } = true;

    private FakeSite(WebApplication app)
    {
        _app = app;
    }

    public string Url => _app.Urls.First() + "/";

    public IReadOnlyList<Request> Requests => _requests.ToList();

    public IReadOnlyList<string> PageRequests => Requests.Select(r => r.Path).Where(p => p is not "/robots.txt" and not "/sitemap.xml").ToList();

    public void ClearRequests() => _requests.Clear();

    public static async Task<FakeSite> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var site = new FakeSite(app);

        app.Map("/{**path}", (HttpRequest request, HttpResponse response) =>
        {
            var path = request.Path.Value!;
            site._requests.Enqueue(new Request(path, request.Headers.IfNoneMatch.FirstOrDefault(), request.Headers.IfModifiedSince.FirstOrDefault()));

            if (path == "/robots.txt")
            {
                return Results.Text("User-agent: *\nAllow: /\n");
            }

            if (path == "/sitemap.xml")
            {
                if (!site.Sitemap)
                {
                    return Results.NotFound();
                }

                var urls = new StringBuilder("""<?xml version="1.0" encoding="UTF-8"?><urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">""");
                foreach (var (pagePath, page) in site.Pages.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    urls.Append("<url><loc>").Append(site.Url.TrimEnd('/')).Append(pagePath).Append("</loc>");
                    if (page.Lastmod is { } lastmod)
                    {
                        urls.Append("<lastmod>").Append(lastmod.ToString("yyyy-MM-ddTHH:mm:sszzz")).Append("</lastmod>");
                    }

                    urls.Append("</url>");
                }

                return Results.Text(urls.Append("</urlset>").ToString(), "application/xml");
            }

            if (!site.Pages.TryGetValue(path, out var found))
            {
                return Results.NotFound();
            }

            if (found.ETag is not null)
            {
                response.Headers.ETag = found.ETag;
            }

            if (found.LastModified is not null)
            {
                response.Headers.LastModified = found.LastModified;
            }

            var etagMatches = found.ETag is not null && request.Headers.IfNoneMatch.ToString() == found.ETag;
            var dateMatches = found.LastModified is not null && request.Headers.IfModifiedSince.ToString() == found.LastModified;
            if (etagMatches || dateMatches)
            {
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }

            return Results.Text(found.Html, "text/html");
        });

        await app.StartAsync();
        return site;
    }

    public static string Html(string title, string body, bool noIndex = false) =>
        $"<html><head><title>{title}</title>{(noIndex ? "<meta name=\"robots\" content=\"noindex\">" : "")}</head><body><main><h1>{title}</h1><p>{body}</p></main></body></html>";

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
