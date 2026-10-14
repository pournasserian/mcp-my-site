using System.Globalization;
using System.Text;

namespace McpMySite.Server.Pages;

/// <summary>
/// /llms.txt in the llmstxt.org format: the site's pages as a Markdown list for language models, grouped by section,
/// with how to reach this server. Built once per loaded index and base URL, since it reads every page.
/// </summary>
public sealed class LlmsTxt(IndexHolder holder, ILogger<LlmsTxt> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (DateTimeOffset BuiltAt, string BaseUrl, string Text)? _cached;

    /// <returns>The text, or null while no index is loaded.</returns>
    public async Task<string?> GetAsync(SiteInfo info, string baseUrl, CancellationToken cancellationToken)
    {
        var store = holder.Current;
        if (store is null || info.Site is null || info.Index is null)
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_cached is { } cached && cached.BuiltAt == store.Manifest.BuiltAt && cached.BaseUrl == baseUrl)
            {
                return cached.Text;
            }

            var stopwatch = Stopwatch.StartNew();
            var pages = new List<PageDocument>();
            foreach (var (url, _) in store.ListPages())
            {
                if (await store.GetPageAsync(url, cancellationToken) is { } page)
                {
                    pages.Add(page);
                }
            }

            var text = Build(info, pages);
            _cached = (store.Manifest.BuiltAt, baseUrl, text);
            logger.LogInformation("Built llms.txt for {BaseUrl}: {Pages} pages, {Chars} characters in {ElapsedMs:F0} ms", baseUrl, pages.Count, text.Length, stopwatch.Elapsed.TotalMilliseconds);
            return text;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static string Build(SiteInfo info, IReadOnlyList<PageDocument> pages)
    {
        var site = info.Site!;
        var index = info.Index!;
        var sb = new StringBuilder();
        sb.Append($"# {Inline(site.Name)}\n\n");
        if (!string.IsNullOrWhiteSpace(site.Description))
        {
            sb.Append($"> {Inline(site.Description)}\n\n");
        }

        sb.Append($"The {index.PageCount} pages of {site.Url}, as indexed on {index.BuiltAt.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}. ");
        sb.Append($"AI assistants can search and read them through the MCP server at {info.Endpoints.Mcp} (tools `search` and `fetch`)");
        sb.Append(info.Endpoints.SearchApi is null ? ".\n" : $", and scripts through {info.Endpoints.SearchApi}?q=.\n");

        // Sections in the order and with the names the index recorded; older indexes fall back to path and size.
        var titles = index.Stats?.Sections.ToDictionary(s => s.Path, s => s.Title) ?? [];
        var order = index.Stats?.Sections.Select((s, i) => (s.Path, i)).ToDictionary(x => x.Path, x => x.i) ?? [];
        var sections = pages
            .GroupBy(p => SectionPath(p.Url))
            .OrderBy(g => order.GetValueOrDefault(g.Key, int.MaxValue))
            .ThenByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal);

        foreach (var section in sections)
        {
            var heading = section.Key == "/" ? "Home" : titles.GetValueOrDefault(section.Key) ?? section.Key;
            sb.Append($"\n## {Inline(heading)}\n\n");
            foreach (var page in section.OrderByDescending(p => p.Modified ?? DateTimeOffset.MinValue).ThenBy(p => p.Url, StringComparer.Ordinal))
            {
                var description = Inline(page.Description);
                sb.Append($"- [{LinkText(page.Title)}]({page.Url})");
                sb.Append(description.Length > 0 ? $": {description}\n" : "\n");
            }
        }

        return sb.ToString();
    }

    private static string SectionPath(string pageUrl)
    {
        var path = new Uri(pageUrl).AbsolutePath.Trim('/');
        return path.Length == 0 ? "/" : "/" + path.Split('/')[0];
    }

    private static string Inline(string? text) =>
        string.IsNullOrWhiteSpace(text) ? string.Empty : string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string LinkText(string title) => Inline(title).Replace("[", "\\[", StringComparison.Ordinal).Replace("]", "\\]", StringComparison.Ordinal);
}
