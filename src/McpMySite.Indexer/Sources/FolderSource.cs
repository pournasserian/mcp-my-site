namespace McpMySite.Indexer.Sources;

/// <summary>Reads built HTML from a local folder and maps file paths to URLs under the site base URL.</summary>
public sealed class FolderSource(string folder, Uri siteUrl, FolderUrlStyle urlStyle, GlobMatcher filter, int maxPages, ILogger? logger = null) : ISiteSource
{
    private readonly List<SourceError> _errors = [];
    private readonly ILogger _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public IReadOnlyList<SourceError> Errors => _errors;

    public async IAsyncEnumerable<RawPage> GetPagesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!Directory.Exists(folder))
        {
            throw new DirectoryNotFoundException($"Source folder not found: {folder}");
        }

        var root = Path.GetFullPath(folder);
        var files = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal);

        _logger.LogInformation("Reading HTML files from {Folder}", root);
        var count = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (count >= maxPages)
            {
                _logger.LogInformation("Reached Crawl:MaxPages ({MaxPages}); the remaining files were not read", maxPages);
                break;
            }

            var url = ToUrl(root, file);
            if (!filter.IsAllowed(new Uri(url).AbsolutePath))
            {
                _logger.LogDebug("Skipping {File}: {Url} is excluded by Crawl:Include or Crawl:Exclude", file, url);
                continue;
            }

            string html;
            try
            {
                html = await File.ReadAllTextAsync(file, cancellationToken);
            }
            catch (IOException ex)
            {
                _errors.Add(new SourceError(url, ex.Message));
                _logger.LogWarning("Failed to read {File}: {Message}", file, ex.Message);
                continue;
            }

            count++;
            _logger.LogDebug("Read {File} as {Url} ({Chars} characters)", file, url, html.Length);
            yield return new RawPage(url, html, File.GetLastWriteTimeUtc(file));
        }

        _logger.LogInformation("Read {Count} pages from {Folder}, {Errors} failed", count, root, _errors.Count);
    }

    internal string ToUrl(string root, string file)
    {
        var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
        if (urlStyle == FolderUrlStyle.Clean)
        {
            var name = Path.GetFileName(relative);
            if (name.Equals("index.html", StringComparison.OrdinalIgnoreCase) || name.Equals("index.htm", StringComparison.OrdinalIgnoreCase))
            {
                relative = relative[..^name.Length];
            }
            else
            {
                relative = Path.ChangeExtension(relative, null);
            }
        }

        var path = "/" + relative.TrimStart('/');
        return new Uri(siteUrl, path).ToString();
    }
}
