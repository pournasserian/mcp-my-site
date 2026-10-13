using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using McpMySite.Core.Search;
using ReverseMarkdown;
using System.Globalization;
using System.Text.RegularExpressions;

namespace McpMySite.Indexer.Extraction;

/// <summary>
/// Turns an HTML page into Markdown plus metadata. Content is picked automatically: &lt;main&gt; or
/// &lt;article&gt; first, then a text-density heuristic. There are no per-site selector overrides by design.
/// </summary>
public sealed partial class HtmlExtractor
{
    private static readonly string[] NoiseSelectors =
    [
        "script", "style", "noscript", "template", "svg", "canvas", "iframe", "object", "embed",
        "form", "button", "input", "select", "textarea", "nav", "aside", "footer",
        "[role=navigation]", "[role=banner]", "[role=contentinfo]", "[role=complementary]", "[role=search]",
        "[aria-hidden=true]", "[hidden]", ".skip-link", ".visually-hidden", ".sr-only",
    ];

    private static readonly string[] CandidateSelectors = ["div", "section", "td", "body"];

    private readonly HtmlParser _parser = new();
    private readonly Converter _markdown = new(new Config
    {
        // GithubFlavored on the default writer gives fenced code and real Markdown; the separate
        // MarkdownFlavor.GitHub writer passes block HTML through untouched, which is not what we want.
        GithubFlavored = true,
        Formatting = { RemoveComments = true, CleanupSpaces = true },
        Links = { SmartHref = true },
        Tags = { Unknown = Config.UnknownTagsOption.Bypass },
    });

    public ExtractedPage Extract(string html, string url, DateTimeOffset? lastModifiedHint = null)
    {
        var document = _parser.ParseDocument(html);
        var pageUri = new Uri(url);

        var canonical = document.QuerySelector("link[rel~=canonical]")?.GetAttribute("href");
        if (!string.IsNullOrWhiteSpace(canonical) && Uri.TryCreate(pageUri, canonical, out var canonicalUri)
            && string.Equals(canonicalUri.Host, pageUri.Host, StringComparison.OrdinalIgnoreCase))
        {
            pageUri = new UriBuilder(canonicalUri) { Fragment = string.Empty }.Uri;
        }

        var siteName = Meta(document, "property", "og:site_name");
        var robots = (Meta(document, "name", "robots") ?? string.Empty) + " " + (Meta(document, "name", "googlebot") ?? string.Empty);
        var noIndex = robots.Contains("noindex", StringComparison.OrdinalIgnoreCase);

        var (content, source) = SelectContent(document);
        CleanContent(content);
        AbsolutizeLinks(content, pageUri);

        var headings = content.QuerySelectorAll("h1, h2, h3, h4, h5, h6")
            .Select(h => Collapse(h.TextContent))
            .Where(h => h.Length > 0)
            .ToList();

        var title = ResolveTitle(document, content, siteName, pageUri);
        var markdown = ToMarkdown(content);
        var textLength = Snippets.Flatten(markdown).Length;

        return new ExtractedPage
        {
            Url = pageUri.ToString(),
            Title = title,
            Description = Meta(document, "name", "description") ?? Meta(document, "property", "og:description"),
            Language = ResolveLanguage(document),
            Headings = headings,
            Modified = ResolveModified(document, content, lastModifiedHint),
            Markdown = markdown,
            TextLength = textLength,
            NoIndex = noIndex,
            SiteName = siteName,
            ContentSource = source,
        };
    }

    private static (IElement Element, string Source) SelectContent(IHtmlDocument document)
    {
        var body = document.Body ?? document.DocumentElement;

        var mains = document.QuerySelectorAll("main").ToList();
        var main = mains.Count == 1 ? mains[0] : mains.FirstOrDefault(m => m.TextContent.Trim().Length > 0);
        if (main is not null)
        {
            var innerArticles = main.QuerySelectorAll("article").ToList();
            if (innerArticles.Count == 1 && innerArticles[0].TextContent.Length >= main.TextContent.Length * 0.5)
            {
                return (innerArticles[0], "article");
            }

            return (main, "main");
        }

        var articles = document.QuerySelectorAll("article").ToList();
        if (articles.Count == 1)
        {
            return (articles[0], "article");
        }

        return (AutoDetect(body), "auto");
    }

    /// <summary>Scores block containers by text length weighted against link density and returns the best one.</summary>
    private static IElement AutoDetect(IElement body)
    {
        var scratch = (IElement)body.Clone(true);
        foreach (var node in scratch.QuerySelectorAll(string.Join(",", NoiseSelectors)).ToList())
        {
            node.Remove();
        }

        IElement? best = null;
        var bestScore = 0.0;
        foreach (var candidate in scratch.QuerySelectorAll(string.Join(",", CandidateSelectors)))
        {
            var text = Collapse(candidate.TextContent);
            if (text.Length < 100)
            {
                continue;
            }

            var linkText = candidate.QuerySelectorAll("a").Sum(a => Collapse(a.TextContent).Length);
            var linkDensity = Math.Min(1.0, (double)linkText / text.Length);
            // Penalise containers that are mostly links (menus, tag clouds) and favour direct text.
            var ownText = candidate.ChildNodes.Where(n => n.NodeType == NodeType.Text || n.NodeName is "P" or "H1" or "H2" or "H3" or "UL" or "OL" or "PRE" or "BLOCKQUOTE" or "TABLE")
                .Sum(n => Collapse(n.TextContent).Length);
            var score = (ownText + text.Length * 0.25) * Math.Pow(1 - linkDensity, 2);
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        if (best is null)
        {
            return scratch;
        }

        // Prefer the smallest ancestor that still holds most of the best block's text; this re-attaches
        // sibling paragraphs that the density score split off.
        var chosen = best;
        var chosenLength = Collapse(chosen.TextContent).Length;
        for (var parent = chosen.ParentElement; parent is not null && parent != scratch; parent = parent.ParentElement)
        {
            var parentLength = Collapse(parent.TextContent).Length;
            if (parentLength <= chosenLength * 1.6)
            {
                chosen = parent;
                chosenLength = parentLength;
            }
            else
            {
                break;
            }
        }

        return chosen;
    }

    private static void CleanContent(IElement content)
    {
        var selector = string.Join(",", NoiseSelectors.Where(s => s != "footer"));
        foreach (var node in content.QuerySelectorAll(selector).ToList())
        {
            node.Remove();
        }

        // Footers inside the content block are usually page-level chrome unless the block is the footer's parent article.
        foreach (var footer in content.QuerySelectorAll("footer").ToList())
        {
            if (footer.QuerySelectorAll("a").Count() >= 3 || Collapse(footer.TextContent).Length < 40)
            {
                footer.Remove();
            }
        }

        foreach (var comment in content.Descendants<IComment>().ToList())
        {
            comment.Remove();
        }
    }

    private static void AbsolutizeLinks(IElement content, Uri pageUri)
    {
        foreach (var anchor in content.QuerySelectorAll("a[href]"))
        {
            var href = anchor.GetAttribute("href");
            if (!string.IsNullOrEmpty(href) && !href.StartsWith('#') && Uri.TryCreate(pageUri, href, out var absolute))
            {
                anchor.SetAttribute("href", absolute.ToString());
            }
        }

        foreach (var image in content.QuerySelectorAll("img[src]"))
        {
            var src = image.GetAttribute("src");
            if (!string.IsNullOrEmpty(src) && Uri.TryCreate(pageUri, src, out var absolute))
            {
                image.SetAttribute("src", absolute.ToString());
            }
        }
    }

    private string ToMarkdown(IElement content)
    {
        var markdown = _markdown.Convert(content.OuterHtml);
        markdown = markdown.Replace("\r\n", "\n");
        markdown = TrailingSpacesRegex().Replace(markdown, string.Empty);
        markdown = ExcessBlankLinesRegex().Replace(markdown, "\n\n");
        return markdown.Trim();
    }

    private static string ResolveTitle(IHtmlDocument document, IElement content, string? siteName, Uri pageUri)
    {
        var title = Collapse(document.Title ?? string.Empty);
        var ogTitle = Meta(document, "property", "og:title");
        if (title.Length == 0 && !string.IsNullOrWhiteSpace(ogTitle))
        {
            title = Collapse(ogTitle);
        }

        title = StripSiteSuffix(title, siteName);

        if (title.Length == 0)
        {
            var h1 = content.QuerySelector("h1");
            title = h1 is null ? string.Empty : Collapse(h1.TextContent);
        }

        if (title.Length == 0)
        {
            title = pageUri.AbsolutePath.Trim('/').Length == 0 ? pageUri.Host : pageUri.AbsolutePath.Trim('/');
        }

        return title;
    }

    internal static string StripSiteSuffix(string title, string? siteName)
    {
        var match = TitleSeparatorRegex().Match(title);
        if (!match.Success)
        {
            return title;
        }

        var head = title[..match.Index].Trim();
        var tail = title[(match.Index + match.Length)..].Trim();
        if (head.Length == 0)
        {
            return title;
        }

        if (!string.IsNullOrWhiteSpace(siteName))
        {
            return string.Equals(tail, siteName.Trim(), StringComparison.OrdinalIgnoreCase) ? head : title;
        }

        // Without a known site name, only strip a short trailing brand when the head is the longer part.
        return tail.Length <= 40 && head.Length >= tail.Length ? head : title;
    }

    private static string ResolveLanguage(IHtmlDocument document)
    {
        var lang = document.DocumentElement?.GetAttribute("lang");
        if (!string.IsNullOrWhiteSpace(lang))
        {
            return lang.Trim();
        }

        var locale = Meta(document, "property", "og:locale");
        if (!string.IsNullOrWhiteSpace(locale))
        {
            return locale.Trim().Replace('_', '-');
        }

        var httpEquiv = document.QuerySelector("meta[http-equiv=content-language]")?.GetAttribute("content");
        return string.IsNullOrWhiteSpace(httpEquiv) ? "und" : httpEquiv.Trim();
    }

    private static DateTimeOffset? ResolveModified(IHtmlDocument document, IElement content, DateTimeOffset? hint)
    {
        var candidates = new[]
        {
            Meta(document, "property", "article:modified_time"),
            Meta(document, "property", "og:updated_time"),
            Meta(document, "name", "last-modified"),
            Meta(document, "name", "date"),
            Meta(document, "property", "article:published_time"),
            Meta(document, "name", "dcterms.modified"),
            Meta(document, "name", "dcterms.date"),
            content.QuerySelector("time[datetime]")?.GetAttribute("datetime"),
        };

        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate)
                && DateTimeOffset.TryParse(candidate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            {
                return parsed;
            }
        }

        return hint;
    }

    private static string? Meta(IHtmlDocument document, string attribute, string value)
    {
        var content = document.QuerySelector($"meta[{attribute}='{value}' i]")?.GetAttribute("content");
        return string.IsNullOrWhiteSpace(content) ? null : System.Net.WebUtility.HtmlDecode(content.Trim());
    }

    private static string Collapse(string text) => WhitespaceRegex().Replace(text, " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"[ \t]+$", RegexOptions.Multiline)]
    private static partial Regex TrailingSpacesRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExcessBlankLinesRegex();

    [GeneratedRegex(@"\s+(\||–|—|-|·|•|::|:)\s+(?=[^|–—\-·•:]*$)")]
    private static partial Regex TitleSeparatorRegex();
}
