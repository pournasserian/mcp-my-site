using McpMySite.Indexer.Extraction;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace McpMySite.Indexer.Sources;

/// <summary>
/// SQLite record of what the last crawl saw per page: the HTTP validators, the sitemap lastmod, the extraction and
/// the outbound links, so an unchanged page can be replayed without downloading it again.
/// </summary>
public sealed class PageStore : IDisposable
{
    public const string FileName = "page-store.sqlite";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SqliteConnection _connection;
    private readonly string _site;

    public sealed record Entry(
        string Url,
        string? ETag,
        string? LastModified,
        DateTimeOffset? SitemapLastmod,
        ExtractedPage Page,
        IReadOnlyList<string> Links,
        DateTimeOffset FetchedAt);

    public PageStore(string path, Uri siteUrl)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _site = siteUrl.ToString();
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _connection.Open();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS pages (
                site TEXT NOT NULL, url TEXT NOT NULL, etag TEXT, last_modified TEXT, sitemap_lastmod TEXT,
                page TEXT NOT NULL, links TEXT NOT NULL, fetched_at TEXT NOT NULL,
                PRIMARY KEY (site, url))
            """;
        cmd.ExecuteNonQuery();
    }

    public static string PathFor(string cachePath) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(cachePath)) ?? ".", FileName);

    public Entry? Get(string url)
    {
        lock (_connection)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT etag, last_modified, sitemap_lastmod, page, links, fetched_at FROM pages WHERE site = $site AND url = $url";
            cmd.Parameters.AddWithValue("$site", _site);
            cmd.Parameters.AddWithValue("$url", url);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            var page = JsonSerializer.Deserialize<ExtractedPage>(reader.GetString(3), JsonOptions);
            var links = JsonSerializer.Deserialize<List<string>>(reader.GetString(4), JsonOptions);
            if (page is null || links is null)
            {
                return null;
            }

            return new Entry(
                url,
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : DateTimeOffset.Parse(reader.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind),
                page,
                links,
                DateTimeOffset.Parse(reader.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind));
        }
    }

    public void Put(Entry entry)
    {
        lock (_connection)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO pages (site, url, etag, last_modified, sitemap_lastmod, page, links, fetched_at)
                VALUES ($site, $url, $etag, $lastModified, $sitemapLastmod, $page, $links, $fetchedAt)
                """;
            cmd.Parameters.AddWithValue("$site", _site);
            cmd.Parameters.AddWithValue("$url", entry.Url);
            cmd.Parameters.AddWithValue("$etag", (object?)entry.ETag ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$lastModified", (object?)entry.LastModified ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$sitemapLastmod", (object?)entry.SitemapLastmod?.ToString("O") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$page", JsonSerializer.Serialize(entry.Page, JsonOptions));
            cmd.Parameters.AddWithValue("$links", JsonSerializer.Serialize(entry.Links, JsonOptions));
            cmd.Parameters.AddWithValue("$fetchedAt", entry.FetchedAt.ToString("O"));
            cmd.ExecuteNonQuery();
        }
    }

    public void Dispose() => _connection.Dispose();
}
