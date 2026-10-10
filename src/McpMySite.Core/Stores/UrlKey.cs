namespace McpMySite.Core.Stores;

/// <summary>Normalises URLs so lookups tolerate scheme, host case, trailing slash and fragment differences.</summary>
public static class UrlKey
{
    public static string Normalize(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return url.Trim().TrimEnd('/').ToLowerInvariant();
        }

        var path = uri.AbsolutePath;
        if (path.Length > 1)
        {
            path = path.TrimEnd('/');
        }

        return $"{uri.Host.ToLowerInvariant()}{path}{uri.Query}";
    }
}
