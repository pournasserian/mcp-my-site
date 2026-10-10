using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace McpMySite.Core.Search;

/// <summary>Builds a short excerpt around the first query term and marks every matched term.</summary>
public static partial class Snippets
{
    public const int DefaultLength = 240;

    public static (string Plain, string Highlighted) Build(string text, string query, int length = DefaultLength)
    {
        var flat = Flatten(text);
        var terms = Tokenizer.Tokenize(query).Distinct().ToList();
        if (flat.Length == 0)
        {
            return (string.Empty, string.Empty);
        }

        var start = 0;
        if (terms.Count > 0)
        {
            var first = FindFirst(flat, terms);
            if (first > 0)
            {
                start = Math.Max(0, first - length / 3);
                var space = flat.LastIndexOf(' ', Math.Min(start, flat.Length - 1));
                start = space > 0 && start - space < 40 ? space + 1 : start;
            }
        }

        var end = Math.Min(flat.Length, start + length);
        if (end < flat.Length)
        {
            var space = flat.LastIndexOf(' ', end - 1, Math.Min(40, end));
            if (space > start)
            {
                end = space;
            }
        }

        var excerpt = flat[start..end].Trim();
        var plain = (start > 0 ? "…" : string.Empty) + excerpt + (end < flat.Length ? "…" : string.Empty);
        return (plain, Highlight(plain, terms));
    }

    private static int FindFirst(string flat, List<string> terms)
    {
        var best = -1;
        foreach (var term in terms)
        {
            var match = WordRegex(term).Match(flat);
            if (match.Success && (best < 0 || match.Index < best))
            {
                best = match.Index;
            }
        }

        return Math.Max(best, 0);
    }

    private static string Highlight(string plain, List<string> terms)
    {
        if (terms.Count == 0)
        {
            return WebUtility.HtmlEncode(plain);
        }

        var pattern = string.Join("|", terms.OrderByDescending(t => t.Length).Select(Regex.Escape));
        var regex = new Regex(
            @"(?<![\p{L}\p{N}])(?:" + pattern + @")(?![\p{L}\p{N}])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        var sb = new StringBuilder();
        var last = 0;
        foreach (Match m in regex.Matches(plain))
        {
            sb.Append(WebUtility.HtmlEncode(plain[last..m.Index]));
            sb.Append("<mark>").Append(WebUtility.HtmlEncode(m.Value)).Append("</mark>");
            last = m.Index + m.Length;
        }

        sb.Append(WebUtility.HtmlEncode(plain[last..]));
        return sb.ToString();
    }

    private static Regex WordRegex(string term) =>
        new(@"(?<![\p{L}\p{N}])" + Regex.Escape(term) + @"(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Removes Markdown syntax and collapses whitespace so excerpts read as prose.</summary>
    public static string Flatten(string markdown)
    {
        var s = MarkdownImageRegex().Replace(markdown, string.Empty);
        s = MarkdownLinkRegex().Replace(s, "$1");
        s = MarkdownSyntaxRegex().Replace(s, string.Empty);
        s = WhitespaceRegex().Replace(s, " ");
        return s.Trim();
    }

    [GeneratedRegex(@"!\[[^\]]*\]\([^)]*\)")]
    private static partial Regex MarkdownImageRegex();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)")]
    private static partial Regex MarkdownLinkRegex();

    [GeneratedRegex(@"(?m)^\s{0,3}(#{1,6}\s+|>\s?|[-*+]\s+|\d+\.\s+)|[*_`~]{1,3}|\|")]
    private static partial Regex MarkdownSyntaxRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
