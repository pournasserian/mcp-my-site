using System.Text.RegularExpressions;

namespace McpMySite.Indexer.Build;

/// <summary>
/// Profiles an index into the signals of explore.json: which kinds of content the site has and how much. Views
/// built later declare the signals they need, so a view is hidden with a reason instead of drawn empty. Everything
/// here is counted from the index itself; nothing is fetched or sent anywhere.
/// </summary>
public static partial class SignalProbe
{
    public static ExploreBundle Profile(Uri siteUri, IReadOnlyList<PageDocument> pages, IReadOnlyList<ChunkRecord> chunks, bool keywordOnly, BuildReport report, DateTimeOffset builtAt)
    {
        var shapes = pages.Select(p => Shape.Of(p.Markdown, siteUri)).ToList();
        var sections = SiteOutline.Sections(pages).Where(s => s.Path != "/").ToList();
        var chunksPerPage = chunks.GroupBy(c => c.PageId).ToDictionary(g => g.Key, g => g.Count());
        var knownLanguages = pages.Where(p => p.Language != "und").ToList();

        var profile = new Dictionary<string, Signal>
        {
            [SignalNames.Sections] = new(sections.Sum(s => s.Pages.Count), sections.Count),
            [SignalNames.Headings] = new(pages.Count(p => p.Headings.Count > 0), pages.Sum(p => p.Headings.Count)),
            [SignalNames.Words] = new(shapes.Count(s => s.Words > 0), shapes.Sum(s => s.Words)),
            [SignalNames.Chunks] = new(chunksPerPage.Count, chunks.Count),
            [SignalNames.Dates] = Same(pages.Count(p => p.Modified is not null)),
            [SignalNames.Languages] = new(knownLanguages.Count, knownLanguages.Select(p => p.Language).Distinct(StringComparer.OrdinalIgnoreCase).Count()),
            [SignalNames.InternalLinks] = Sum(shapes, s => s.InternalLinks),
            [SignalNames.ExternalLinks] = Sum(shapes, s => s.ExternalLinks),
            [SignalNames.Images] = Sum(shapes, s => s.Images),
            [SignalNames.Tables] = Sum(shapes, s => s.Tables),
            [SignalNames.CodeBlocks] = Sum(shapes, s => s.CodeBlocks),
            [SignalNames.CodeSpans] = Sum(shapes, s => s.CodeSpans),
            [SignalNames.ListItems] = Sum(shapes, s => s.ListItems),
            [SignalNames.Embeddings] = keywordOnly ? Signal.None : new(chunksPerPage.Count, chunks.Count),
            [SignalNames.ThinPages] = Same(report.LowTextPages.Count),
            [SignalNames.AutoExtracted] = Same(report.ContentSources.GetValueOrDefault("auto")),
        };

        return new ExploreBundle
        {
            FormatVersion = ExploreBundle.CurrentFormatVersion,
            SiteUrl = siteUri.ToString(),
            BuiltAt = builtAt,
            PageCount = pages.Count,
            ChunkCount = chunks.Count,
            Profile = profile,
        };
    }

    private static Signal Same(int pages) => new(pages, pages);

    private static Signal Sum(List<Shape> shapes, Func<Shape, int> count) => new(shapes.Count(s => count(s) > 0), shapes.Sum(count));

    /// <summary>Counts of the Markdown constructs on one page. Fenced code is skipped so its contents count as nothing but code.</summary>
    private sealed record Shape(int Words, int InternalLinks, int ExternalLinks, int Images, int Tables, int CodeBlocks, int CodeSpans, int ListItems)
    {
        public static Shape Of(string markdown, Uri siteUri)
        {
            int internalLinks = 0, externalLinks = 0, images = 0, tables = 0, codeBlocks = 0, codeSpans = 0, listItems = 0;
            var inFence = false;
            foreach (var line in markdown.Replace("\r\n", "\n").Split('\n'))
            {
                if (FenceRegex().IsMatch(line))
                {
                    if (!inFence)
                    {
                        codeBlocks++;
                    }

                    inFence = !inFence;
                    continue;
                }

                if (inFence)
                {
                    continue;
                }

                if (TableSeparatorRegex().IsMatch(line))
                {
                    tables++;
                }

                if (ListItemRegex().IsMatch(line))
                {
                    listItems++;
                }

                images += ImageRegex().Count(line);
                codeSpans += CodeSpanRegex().Count(line);
                foreach (Match link in LinkRegex().Matches(line))
                {
                    switch (Classify(link.Groups[1].Value, siteUri))
                    {
                        case LinkKind.Internal: internalLinks++; break;
                        case LinkKind.External: externalLinks++; break;
                    }
                }
            }

            var words = markdown.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
            return new Shape(words, internalLinks, externalLinks, images, tables, codeBlocks, codeSpans, listItems);
        }
    }

    private enum LinkKind { Skip, Internal, External }

    /// <summary>Fragments and non-web schemes (mailto, tel) are not page links; a relative target can only be on the site itself.</summary>
    private static LinkKind Classify(string target, Uri siteUri)
    {
        if (target.Length == 0 || target[0] == '#')
        {
            return LinkKind.Skip;
        }

        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri))
        {
            return LinkKind.Internal;
        }

        if (uri.Scheme is not ("http" or "https"))
        {
            return LinkKind.Skip;
        }

        return string.Equals(uri.Host, siteUri.Host, StringComparison.OrdinalIgnoreCase) ? LinkKind.Internal : LinkKind.External;
    }

    [GeneratedRegex(@"^\s{0,3}(```|~~~)")]
    private static partial Regex FenceRegex();

    // A table's header separator: only pipes, dashes, colons and spaces, with at least one pipe and a dash run.
    [GeneratedRegex(@"^(?=.*\|)(?=.*---)[\s|:\-]+$")]
    private static partial Regex TableSeparatorRegex();

    [GeneratedRegex(@"^\s*(?:[-*+]|\d{1,9}[.)])\s+\S")]
    private static partial Regex ListItemRegex();

    [GeneratedRegex(@"!\[[^\]]*\]\([^)]*\)")]
    private static partial Regex ImageRegex();

    // Link text without nested brackets; the target stops at whitespace so an optional "title" is left out.
    [GeneratedRegex(@"(?<!!)\[[^\]]*\]\(\s*<?([^\s)>]*)>?[^)]*\)")]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"`+[^`\n]+`+")]
    private static partial Regex CodeSpanRegex();
}
