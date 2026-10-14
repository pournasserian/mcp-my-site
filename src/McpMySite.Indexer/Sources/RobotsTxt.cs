namespace McpMySite.Indexer.Sources;

/// <summary>Minimal robots.txt reader: Disallow/Allow rules for our agent or "*", plus Sitemap lines.</summary>
public sealed class RobotsTxt
{
    private readonly List<(bool Allow, string Path)> _rules;

    private RobotsTxt(List<(bool, string)> rules, List<string> sitemaps)
    {
        _rules = rules;
        Sitemaps = sitemaps;
    }

    public IReadOnlyList<string> Sitemaps { get; }

    /// <summary>Allow and Disallow rules that apply to this crawler.</summary>
    public int RuleCount => _rules.Count;

    public static RobotsTxt Empty { get; } = new([], []);

    public static RobotsTxt Parse(string content, string userAgentToken)
    {
        var sitemaps = new List<string>();
        var groups = new List<(List<string> Agents, List<(bool, string)> Rules)>();
        List<string>? agents = null;
        List<(bool, string)>? rules = null;

        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Split('#')[0].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon < 0)
            {
                continue;
            }

            var field = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();
            switch (field)
            {
                case "sitemap":
                    sitemaps.Add(value);
                    break;
                case "user-agent":
                    if (rules is { Count: > 0 } || agents is null)
                    {
                        agents = [];
                        rules = [];
                        groups.Add((agents, rules));
                    }

                    agents.Add(value.ToLowerInvariant());
                    break;
                case "disallow":
                case "allow":
                    if (rules is null)
                    {
                        break;
                    }

                    if (value.Length > 0)
                    {
                        rules.Add((field == "allow", value));
                    }

                    break;
            }
        }

        var token = userAgentToken.ToLowerInvariant();
        var match = groups.FirstOrDefault(g => g.Agents.Any(a => a != "*" && token.Contains(a)));
        if (match.Rules is null)
        {
            match = groups.FirstOrDefault(g => g.Agents.Contains("*"));
        }

        return new RobotsTxt(match.Rules ?? [], sitemaps);
    }

    public bool IsAllowed(string path)
    {
        (bool Allow, string Path)? best = null;
        foreach (var rule in _rules)
        {
            if (Matches(rule.Path, path) && (best is null || rule.Path.Length > best.Value.Path.Length))
            {
                best = rule;
            }
        }

        return best?.Allow ?? true;
    }

    private static bool Matches(string pattern, string path)
    {
        var anchored = pattern.EndsWith('$');
        if (anchored)
        {
            pattern = pattern[..^1];
        }

        var parts = pattern.Split('*');
        var pos = 0;
        for (var i = 0; i < parts.Length; i++)
        {
            var idx = i == 0 ? (path.StartsWith(parts[0], StringComparison.Ordinal) ? 0 : -1) : path.IndexOf(parts[i], pos, StringComparison.Ordinal);
            if (idx < 0)
            {
                return false;
            }

            pos = idx + parts[i].Length;
        }

        return !anchored || pos == path.Length || parts[^1].Length == 0;
    }
}
