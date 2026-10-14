using System.Net;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace McpMySite.Indexer.Sources;

/// <summary>
/// Live crawl over plain HTTP. Seeds come from the sitemap; when there is none (or when configured) links
/// found in pages are followed too. Only HTML on the site's own host is fetched.
/// </summary>
public sealed partial class CrawlSource : ISiteSource
{
    private const string UserAgentToken = "McpMySiteIndexer";
    private const int MaxHtmlBytes = 5 * 1024 * 1024;

    private static readonly HashSet<string> SkippedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".jpg", ".jpeg", ".png", ".gif", ".webp", ".svg", ".ico", ".css", ".js", ".mjs", ".json", ".xml",
        ".zip", ".gz", ".tar", ".mp3", ".mp4", ".webm", ".woff", ".woff2", ".ttf", ".eot", ".txt", ".rss", ".atom",
    };

    private readonly HttpClient _http;
    private readonly CrawlConfig _config;
    private readonly Uri _siteUrl;
    private readonly GlobMatcher _filter;
    private readonly ILogger _logger;
    private readonly List<SourceError> _errors = [];
    private readonly object _lock = new();

    public CrawlSource(HttpClient http, CrawlConfig config, Uri siteUrl, GlobMatcher filter, ILogger logger)
    {
        _http = http;
        _config = config;
        _siteUrl = siteUrl;
        _filter = filter;
        _logger = logger;
    }

    public IReadOnlyList<SourceError> Errors
    {
        get
        {
            lock (_lock)
            {
                return _errors.ToList();
            }
        }
    }

    /// <summary>Number of URLs that were skipped because robots.txt or a noindex directive asked for it.</summary>
    public int DisallowedCount { get; private set; }

    public async IAsyncEnumerable<RawPage> GetPagesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var robots = _config.RespectRobotsTxt ? await LoadRobotsAsync(cancellationToken) : RobotsTxt.Empty;

        var seeds = new List<(string Url, DateTimeOffset? LastModified)>();
        if (_config.UseSitemap)
        {
            var sitemapUrls = robots.Sitemaps.Count > 0 ? robots.Sitemaps : [new Uri(_siteUrl, "/sitemap.xml").ToString()];
            var entries = await new SitemapReader(_http, _logger).ReadAsync(sitemapUrls, cancellationToken);
            seeds.AddRange(entries.Select(e => (e.Url, e.LastModified)));
            _logger.LogInformation("Sitemap listed {Count} URLs", entries.Count);
        }
        else
        {
            _logger.LogInformation("Crawl:UseSitemap is off; following links from {Url}", _siteUrl);
        }

        var followLinks = _config.AlwaysFollowLinks || seeds.Count == 0;
        if (seeds.Count == 0 && _config.UseSitemap)
        {
            _logger.LogInformation("No sitemap entries; following links from {Url}", _siteUrl);
        }

        if (followLinks)
        {
            seeds.Insert(0, (_siteUrl.ToString(), null));
        }

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<(string Url, DateTimeOffset? LastModified)>();
        foreach (var seed in seeds)
        {
            if (TryNormalize(seed.Url, out var normalized) && visited.Add(Key(normalized)))
            {
                pending.Enqueue((normalized, seed.LastModified));
            }
        }

        var output = Channel.CreateUnbounded<RawPage>();
        var accepted = 0;

        var producer = Task.Run(async () =>
        {
            var inFlight = new List<Task>();
            try
            {
                while (true)
                {
                    (string Url, DateTimeOffset? LastModified)? next = null;
                    lock (_lock)
                    {
                        if (accepted + inFlight.Count < _config.MaxPages && pending.Count > 0)
                        {
                            next = pending.Dequeue();
                        }
                    }

                    if (next is null)
                    {
                        if (inFlight.Count == 0)
                        {
                            int left;
                            lock (_lock)
                            {
                                left = pending.Count;
                            }

                            if (left > 0)
                            {
                                _logger.LogInformation("Reached Crawl:MaxPages ({MaxPages}); {Left} known URLs were not fetched", _config.MaxPages, left);
                            }

                            break;
                        }

                        var done = await Task.WhenAny(inFlight);
                        inFlight.Remove(done);
                        continue;
                    }

                    if (inFlight.Count >= Math.Max(1, _config.Concurrency))
                    {
                        var done = await Task.WhenAny(inFlight);
                        inFlight.Remove(done);
                    }

                    var (url, lastModified) = next.Value;
                    if (!robots.IsAllowed(new Uri(url).PathAndQuery))
                    {
                        lock (_lock)
                        {
                            DisallowedCount++;
                        }

                        _logger.LogDebug("Skipping {Url}: disallowed by robots.txt", url);
                        continue;
                    }

                    inFlight.Add(FetchAsync(url, lastModified));
                }

                await Task.WhenAll(inFlight);
            }
            finally
            {
                output.Writer.Complete();
            }

            async Task FetchAsync(string url, DateTimeOffset? lastModified)
            {
                if (_config.DelayMilliseconds > 0)
                {
                    await Task.Delay(_config.DelayMilliseconds, cancellationToken);
                }

                var page = await TryFetchPageAsync(url, lastModified, cancellationToken);
                if (page is null)
                {
                    return;
                }

                bool accept;
                lock (_lock)
                {
                    accept = accepted < _config.MaxPages;
                    if (accept)
                    {
                        accepted++;
                    }
                }

                if (!accept)
                {
                    return;
                }

                if (followLinks)
                {
                    foreach (var link in ExtractLinks(page.Html, page.Url))
                    {
                        lock (_lock)
                        {
                            if (visited.Add(Key(link)))
                            {
                                pending.Enqueue((link, null));
                            }
                        }
                    }
                }

                await output.Writer.WriteAsync(page, cancellationToken);
            }
        }, cancellationToken);

        await foreach (var page in output.Reader.ReadAllAsync(cancellationToken))
        {
            yield return page;
        }

        await producer;
        _logger.LogInformation(
            "Crawl finished: {Accepted} pages fetched, {Disallowed} skipped by robots.txt or X-Robots-Tag, {Errors} failed",
            accepted, DisallowedCount, Errors.Count);
    }

    private async Task<RobotsTxt> LoadRobotsAsync(CancellationToken cancellationToken)
    {
        var url = new Uri(_siteUrl, "/robots.txt");
        try
        {
            using var response = await _http.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogInformation("No robots.txt at {Url} ({Status}); every page is allowed", url, (int)response.StatusCode);
                return RobotsTxt.Empty;
            }

            var robots = RobotsTxt.Parse(await response.Content.ReadAsStringAsync(cancellationToken), UserAgentToken);
            _logger.LogInformation("Read robots.txt: {Rules} rules for this crawler, {Sitemaps} sitemaps", robots.RuleCount, robots.Sitemaps.Count);
            return robots;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning("Could not read robots.txt: {Message}", ex.Message);
            return RobotsTxt.Empty;
        }
    }

    private async Task<RawPage?> TryFetchPageAsync(string url, DateTimeOffset? lastModified, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await FetchPageAsync(url, lastModified, cancellationToken);
            }
            catch (Exception ex) when (attempt == 1 && IsTransient(ex) && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("Retrying {Url} after: {Message}", url, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException && !cancellationToken.IsCancellationRequested)
            {
                RecordError(url, ex.Message);
                return null;
            }
        }
    }

    private async Task<RawPage?> FetchPageAsync(string url, DateTimeOffset? lastModified, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml;q=0.9,*/*;q=0.1");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            _logger.LogDebug("Skipping {Url}: {Status}", url, (int)response.StatusCode);
            return null;
        }

        if ((int)response.StatusCode >= 500)
        {
            throw new HttpRequestException($"{(int)response.StatusCode} from {url}", null, response.StatusCode);
        }

        if (!response.IsSuccessStatusCode)
        {
            RecordError(url, $"HTTP {(int)response.StatusCode}");
            return null;
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is not null && !mediaType.Contains("html", StringComparison.OrdinalIgnoreCase) && !mediaType.Contains("xhtml", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Skipping {Url}: {MediaType} is not HTML", url, mediaType);
            return null;
        }

        if (response.Headers.TryGetValues("X-Robots-Tag", out var robotsTags) && robotsTags.Any(t => t.Contains("noindex", StringComparison.OrdinalIgnoreCase)))
        {
            lock (_lock)
            {
                DisallowedCount++;
            }

            _logger.LogDebug("Skipping {Url}: X-Robots-Tag noindex", url);
            return null;
        }

        var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? url;
        if (!TryNormalize(finalUrl, out var normalizedFinal))
        {
            _logger.LogDebug("Skipping {Url}: redirected to {FinalUrl}, outside the crawl", url, finalUrl);
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var limited = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            limited.Write(buffer, 0, read);
            if (limited.Length > MaxHtmlBytes)
            {
                RecordError(url, "Page exceeds the 5 MB limit");
                return null;
            }
        }

        var charset = response.Content.Headers.ContentType?.CharSet;
        var encoding = System.Text.Encoding.UTF8;
        if (!string.IsNullOrEmpty(charset))
        {
            try
            {
                encoding = System.Text.Encoding.GetEncoding(charset.Trim('"'));
            }
            catch (ArgumentException)
            {
            }
        }

        var html = encoding.GetString(limited.ToArray());
        var modified = lastModified ?? response.Content.Headers.LastModified;
        _logger.LogDebug("Fetched {Url} ({Status}, {Bytes} bytes)", normalizedFinal, (int)response.StatusCode, limited.Length);
        return new RawPage(normalizedFinal, html, modified);
    }

    private void RecordError(string url, string message)
    {
        _logger.LogWarning("Failed {Url}: {Message}", url, message);
        lock (_lock)
        {
            _errors.Add(new SourceError(url, message));
        }
    }

    private static bool IsTransient(Exception ex) =>
        ex is TaskCanceledException or IOException
        || ex is HttpRequestException { StatusCode: null or >= HttpStatusCode.InternalServerError };

    /// <summary>Absolute, same-host, HTML-looking, allowed by the include/exclude globs, with fragment removed.</summary>
    internal bool TryNormalize(string href, out string normalized)
    {
        normalized = string.Empty;
        if (!Uri.TryCreate(_siteUrl, href, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            return false;
        }

        if (!SameHost(uri.Host, _siteUrl.Host))
        {
            return false;
        }

        var extension = Path.GetExtension(uri.AbsolutePath);
        if (extension.Length > 0 && SkippedExtensions.Contains(extension))
        {
            return false;
        }

        if (!_filter.IsAllowed(uri.AbsolutePath))
        {
            return false;
        }

        var builder = new UriBuilder(uri) { Fragment = string.Empty, Host = _siteUrl.Host, Scheme = _siteUrl.Scheme, Port = _siteUrl.Port };
        normalized = builder.Uri.ToString();
        return true;
    }

    private static bool SameHost(string a, string b)
    {
        static string Strip(string host) => host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
        return string.Equals(Strip(a), Strip(b), StringComparison.OrdinalIgnoreCase);
    }

    private static string Key(string url) => url.TrimEnd('/').ToLowerInvariant();

    internal IEnumerable<string> ExtractLinks(string html, string pageUrl)
    {
        var baseUri = new Uri(pageUrl);
        foreach (Match match in HrefRegex().Matches(html))
        {
            var href = WebUtility.HtmlDecode(match.Groups[1].Value).Trim();
            if (href.Length == 0 || href.StartsWith('#') || href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
                || href.StartsWith("tel:", StringComparison.OrdinalIgnoreCase) || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!Uri.TryCreate(baseUri, href, out var absolute) || !string.IsNullOrEmpty(absolute.Query))
            {
                continue;
            }

            if (TryNormalize(absolute.ToString(), out var normalized))
            {
                yield return normalized;
            }
        }
    }

    [GeneratedRegex("""<a\s[^>]*?href\s*=\s*["']([^"']*)["']""", RegexOptions.IgnoreCase)]
    private static partial Regex HrefRegex();
}
