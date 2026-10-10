using System.Text.RegularExpressions;

namespace McpMySite.Indexer.Configuration;

/// <summary>
/// Matches URL paths against include and exclude globs. "*" matches within one segment, "**" spans segments.
/// A pattern without a leading slash is anchored at the root anyway.
/// </summary>
public sealed class GlobMatcher
{
    private readonly List<Regex> _include;
    private readonly List<Regex> _exclude;

    public GlobMatcher(IEnumerable<string> include, IEnumerable<string> exclude)
    {
        _include = include.Select(ToRegex).ToList();
        _exclude = exclude.Select(ToRegex).ToList();
    }

    public bool IsAllowed(string path)
    {
        if (_exclude.Any(r => r.IsMatch(path)))
        {
            return false;
        }

        return _include.Count == 0 || _include.Any(r => r.IsMatch(path));
    }

    internal static Regex ToRegex(string glob)
    {
        var g = glob.Trim();
        if (!g.StartsWith('/'))
        {
            g = "/" + g;
        }

        var sb = new System.Text.StringBuilder("^");
        for (var i = 0; i < g.Length; i++)
        {
            var c = g[i];
            if (c == '*')
            {
                if (i + 1 < g.Length && g[i + 1] == '*')
                {
                    sb.Append(".*");
                    i++;
                }
                else
                {
                    sb.Append("[^/]*");
                }
            }
            else if (c == '?')
            {
                sb.Append("[^/]");
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
            }
        }

        // "/docs" also matches "/docs/" and everything below it.
        if (!g.EndsWith('*'))
        {
            sb.Append("(/.*)?");
        }

        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
