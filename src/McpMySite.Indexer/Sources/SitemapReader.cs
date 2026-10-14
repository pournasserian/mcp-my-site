using System.IO.Compression;
using System.Xml.Linq;

namespace McpMySite.Indexer.Sources;

/// <summary>Reads sitemap.xml files and sitemap indexes, following nested sitemaps.</summary>
public sealed class SitemapReader(HttpClient http, ILogger logger)
{
    private const int MaxSitemaps = 50;

    public sealed record Entry(string Url, DateTimeOffset? LastModified);

    public async Task<List<Entry>> ReadAsync(IEnumerable<string> sitemapUrls, CancellationToken cancellationToken)
    {
        var results = new List<Entry>();
        var queue = new Queue<string>(sitemapUrls);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (queue.Count > 0 && seen.Count < MaxSitemaps)
        {
            var url = queue.Dequeue();
            if (!seen.Add(url))
            {
                continue;
            }

            XDocument document;
            try
            {
                document = await FetchXmlAsync(url, cancellationToken);
            }
            catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone)
            {
                // The default /sitemap.xml is a guess, so a missing one is normal.
                logger.LogInformation("No sitemap at {Url}", url);
                continue;
            }
            catch (Exception ex) when (ex is HttpRequestException or System.Xml.XmlException or TaskCanceledException)
            {
                logger.LogWarning("Could not read sitemap {Url}: {Message}", url, ex.Message);
                continue;
            }

            var root = document.Root;
            if (root is null)
            {
                continue;
            }

            var ns = root.Name.Namespace;
            if (root.Name.LocalName == "sitemapindex")
            {
                var nested = root.Elements(ns + "sitemap").Elements(ns + "loc").Select(loc => loc.Value.Trim()).ToList();
                nested.ForEach(queue.Enqueue);
                logger.LogDebug("Sitemap index {Url} lists {Count} sitemaps", url, nested.Count);
                continue;
            }

            var before = results.Count;
            foreach (var entry in root.Elements(ns + "url"))
            {
                var loc = entry.Element(ns + "loc")?.Value.Trim();
                if (string.IsNullOrEmpty(loc))
                {
                    continue;
                }

                DateTimeOffset? lastmod = DateTimeOffset.TryParse(entry.Element(ns + "lastmod")?.Value, null, System.Globalization.DateTimeStyles.AssumeUniversal, out var dt) ? dt : null;
                results.Add(new Entry(loc, lastmod));
            }

            logger.LogDebug("Sitemap {Url} lists {Count} URLs", url, results.Count - before);
        }

        var unread = queue.Where(u => !seen.Contains(u)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        if (unread > 0)
        {
            logger.LogWarning("Stopped after {Max} sitemaps; {Unread} more were not read", MaxSitemaps, unread);
        }

        return results;
    }

    private async Task<XDocument> FetchXmlAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var isGzip = url.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
            || response.Content.Headers.ContentType?.MediaType == "application/x-gzip";
        if (isGzip)
        {
            await using var gzip = new GZipStream(stream, CompressionMode.Decompress);
            return await XDocument.LoadAsync(gzip, LoadOptions.None, cancellationToken);
        }

        return await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);
    }
}
