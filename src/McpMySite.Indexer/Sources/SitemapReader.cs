using System.IO.Compression;
using System.Xml.Linq;

namespace McpMySite.Indexer.Sources;

/// <summary>Reads sitemap.xml files and sitemap indexes, following nested sitemaps.</summary>
public sealed class SitemapReader(HttpClient http)
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
            catch (Exception ex) when (ex is HttpRequestException or System.Xml.XmlException or TaskCanceledException)
            {
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
                foreach (var loc in root.Elements(ns + "sitemap").Elements(ns + "loc"))
                {
                    queue.Enqueue(loc.Value.Trim());
                }

                continue;
            }

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
