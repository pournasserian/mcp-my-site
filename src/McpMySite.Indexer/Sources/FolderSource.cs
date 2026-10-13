namespace McpMySite.Indexer.Sources;

/// <summary>Reads built HTML from a local folder and maps file paths to URLs under the site base URL.</summary>
public sealed class FolderSource(string folder, Uri siteUrl, FolderUrlStyle urlStyle, GlobMatcher filter, int maxPages) : ISiteSource
{
    private readonly List<SourceError> _errors = [];

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

        var count = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (count >= maxPages)
            {
                break;
            }

            var url = ToUrl(root, file);
            if (!filter.IsAllowed(new Uri(url).AbsolutePath))
            {
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
                continue;
            }

            count++;
            yield return new RawPage(url, html, File.GetLastWriteTimeUtc(file));
        }
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
