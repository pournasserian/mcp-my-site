using System.Text;

namespace McpMySite.Indexer.Build;

/// <summary>Writes the short site outline that becomes the MCP server instructions.</summary>
public static class SiteOutline
{
    /// <summary>
    /// Upper bound for the generated text. Claude Code is reported to cut server instructions at 2,048 characters, so
    /// the outline is trimmed to fit rather than losing its tail mid-line.
    /// </summary>
    public const int MaxChars = 2000;

    private const int MaxSections = 12;
    private const int MaxTitlesPerSection = 4;
    private const int MaxDescriptionChars = 300;

    // Room kept for the closing "- and N more sections" line while sections are added.
    private const int MoreSectionsReserve = 32;

    /// <summary>Pages grouped by their first path segment, largest first.</summary>
    public sealed record Section(string Path, string? Title, IReadOnlyList<PageDocument> Pages);

    public static string Build(string siteName, string siteUrl, string? siteDescription, IReadOnlyList<PageDocument> pages, DateTimeOffset builtAt, string? preamble)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(preamble))
        {
            sb.Append(preamble.Trim()).Append("\n\n");
        }

        var description = Collapse(siteDescription);
        if (description.Length > MaxDescriptionChars)
        {
            description = description[..MaxDescriptionChars].TrimEnd() + "…";
        }
        sb.Append(description.Length > 0 ? $"{siteName} ({siteUrl}): {description}\n\n" : $"{siteName} ({siteUrl}).\n\n");

        var date = builtAt.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        sb.Append($"This MCP server gives read-only access to the published pages of {siteName}: {Pages(pages.Count)}, a snapshot taken on {date}. ");
        sb.Append($"Use it when the user asks about {siteName} or the subjects the site covers, and whenever an answer should rest on this site rather than on general knowledge.\n\n");

        // Models follow concrete steps better than a general description of the tools.
        sb.Append("How to use it:\n");
        sb.Append("- `search` takes a question or keywords and returns the best matching pages, each with a URL and a snippet.\n");
        sb.Append("- `fetch` takes a URL from the search results and returns the whole page as Markdown. Read the page before answering in detail.\n");
        sb.Append($"- Cite the URL of every page you use. If nothing relevant comes back, say so rather than guessing. Changes made after {date} are not covered.\n");

        var languages = pages.Select(p => p.Language).Where(l => l != "und")
            .GroupBy(l => l).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Key).ToList();
        if (languages.Count > 0)
        {
            sb.Append($"\nContent language: {string.Join(", ", languages)}.\n");
        }

        // The home page is already described by the first line; list the sections under it.
        var sections = Sections(pages).Where(s => s.Path != "/").ToList();
        const string OutlineHeader = "\nSite outline:\n";
        var lines = new List<string>();
        var used = sb.Length + OutlineHeader.Length;
        foreach (var section in sections.Take(MaxSections))
        {
            var line = OutlineLine(section, MaxChars - used - MoreSectionsReserve);
            if (line is null)
            {
                break;
            }

            lines.Add(line);
            used += line.Length;
        }

        if (lines.Count > 0)
        {
            sb.Append(OutlineHeader);
            lines.ForEach(line => sb.Append(line));
            var omitted = sections.Count - lines.Count;
            if (omitted > 0)
            {
                sb.Append($"- and {omitted} more section{(omitted == 1 ? string.Empty : "s")}\n");
            }
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>The outline line for a section with as many page titles as fit in <paramref name="budget"/>; null when none fits.</summary>
    private static string? OutlineLine(Section section, int budget)
    {
        var label = section.Title is null
            ? $"{section.Path} ({Pages(section.Pages.Count)})"
            : $"{section.Title} ({section.Path}, {Pages(section.Pages.Count)})";
        // Newest first, then by URL: crawl order varies between runs and the text should not.
        var titles = section.Pages
            .Where(p => !IsLandingPage(p, section.Path))
            .OrderByDescending(p => p.Modified ?? DateTimeOffset.MinValue)
            .ThenBy(p => p.Url, StringComparer.Ordinal)
            .Select(p => Collapse(p.Title))
            .Where(t => t.Length > 0)
            .Distinct()
            .Take(MaxTitlesPerSection)
            .ToList();

        for (var count = titles.Count; count >= 0; count--)
        {
            var line = count > 0 ? $"- {label}: {string.Join("; ", titles.Take(count))}\n" : $"- {label}\n";
            if (line.Length <= budget)
            {
                return line;
            }
        }

        return null;
    }

    public static IReadOnlyList<Section> Sections(IReadOnlyList<PageDocument> pages) => pages
        .GroupBy(p => SectionPath(p.Url))
        .Select(g => new Section(g.Key, g.FirstOrDefault(p => IsLandingPage(p, g.Key)) is { } landing && Collapse(landing.Title) is { Length: > 0 } title ? title : null, g.ToList()))
        .OrderByDescending(s => s.Pages.Count)
        .ThenBy(s => s.Path, StringComparer.Ordinal)
        .ToList();

    private static string SectionPath(string pageUrl)
    {
        var path = new Uri(pageUrl).AbsolutePath.Trim('/');
        return path.Length == 0 ? "/" : "/" + path.Split('/')[0];
    }

    /// <summary>True for the page that sits at the section's own path, such as /docs for the /docs section.</summary>
    private static bool IsLandingPage(PageDocument page, string sectionPath) =>
        string.Equals("/" + new Uri(page.Url).AbsolutePath.Trim('/'), sectionPath, StringComparison.Ordinal);

    private static string Pages(int count) => $"{count} page{(count == 1 ? string.Empty : "s")}";

    private static string Collapse(string? text) =>
        string.IsNullOrWhiteSpace(text) ? string.Empty : string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
