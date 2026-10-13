using System.Text;

namespace McpMySite.Indexer.Build;

/// <summary>Writes the short site outline that becomes the MCP server instructions.</summary>
public static class SiteOutline
{
    private const int MaxSections = 12;
    private const int MaxTitlesPerSection = 4;

    public static string Build(string siteName, string siteUrl, IReadOnlyList<PageDocument> pages, DateTimeOffset builtAt, string? preamble)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(preamble))
        {
            sb.Append(preamble.Trim()).Append("\n\n");
        }

        sb.Append($"This server searches the content of {siteName} ({siteUrl}). ");
        sb.Append($"It covers {pages.Count} page{(pages.Count == 1 ? string.Empty : "s")}, indexed {builtAt:yyyy-MM-dd}. ");
        sb.Append("Use `search` to find relevant pages and `fetch` with a result URL to read a page in full as Markdown.\n");

        var languages = pages.Select(p => p.Language).Where(l => l != "und").Distinct().ToList();
        if (languages.Count > 0)
        {
            sb.Append($"Content language: {string.Join(", ", languages)}.\n");
        }

        var sections = pages
            .GroupBy(p => Section(p.Url))
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

        if (sections.Count > 1)
        {
            sb.Append("\nSite outline:\n");
            foreach (var group in sections.Take(MaxSections))
            {
                var titles = group.Select(p => p.Title).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().Take(MaxTitlesPerSection);
                sb.Append($"- {group.Key} ({group.Count()} page{(group.Count() == 1 ? string.Empty : "s")}): {string.Join("; ", titles)}\n");
            }

            if (sections.Count > MaxSections)
            {
                sb.Append($"- and {sections.Count - MaxSections} more sections\n");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static string Section(string pageUrl)
    {
        var path = new Uri(pageUrl).AbsolutePath.Trim('/');
        return path.Length == 0 ? "/" : "/" + path.Split('/')[0];
    }
}
