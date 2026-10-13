namespace McpMySite.Indexer.Configuration;

/// <summary>
/// Indexer settings, bound from appsettings.json plus the usual .NET configuration layers
/// (appsettings.{Environment}.json, user secrets in Development, environment variables, --set overrides).
/// Every run is a full rebuild driven by these settings plus CLI overrides.
/// </summary>
public sealed class IndexerConfig
{
    public const string DefaultFileName = "appsettings.json";

    /// <summary>Shipped next to the tool with every setting and its default. Not named appsettings.json so it cannot be mistaken for a site's config.</summary>
    public const string DefaultsFileName = "appsettings.defaults.json";

    public SiteConfig Site { get; set; } = new();

    public SourceConfig Source { get; set; } = new();

    public CrawlConfig Crawl { get; set; } = new();

    public ChunkingConfig Chunking { get; set; } = new();

    public EmbeddingOptions Embeddings { get; set; } = new();

    public CacheConfig Cache { get; set; } = new();

    public OutputConfig Output { get; set; } = new();

    public SafetyConfig Safety { get; set; } = new();

    public InstructionsConfig Instructions { get; set; } = new();

    public void Validate()
    {
        if (!Uri.TryCreate(Site.Url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            throw new InvalidOperationException("Site:Url must be an absolute http(s) URL.");
        }

        if (Source.Type == SourceType.Folder && string.IsNullOrWhiteSpace(Source.Folder))
        {
            throw new InvalidOperationException("Source:Folder is required when Source:Type is \"Folder\".");
        }

        if (Output.Store is not (IndexFormat.JsonStoreType or IndexFormat.SqliteStoreType))
        {
            throw new InvalidOperationException("Output:Store must be \"json\" or \"sqlite\".");
        }

        if (Embeddings.Dimensions <= 0 || string.IsNullOrWhiteSpace(Embeddings.Model))
        {
            throw new InvalidOperationException("Embeddings:Model and Embeddings:Dimensions are required.");
        }
    }
}

public sealed class SiteConfig
{
    /// <summary>Base URL of the site, e.g. https://example.com. Required.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Display name; falls back to og:site_name of the home page, then to the host.</summary>
    public string? Name { get; set; }
}

public enum SourceType
{
    Crawl,
    Folder,
}

public enum FolderUrlStyle
{
    /// <summary>about.html becomes /about and index.html becomes the directory URL.</summary>
    Clean,

    /// <summary>File names are kept as they are: /about.html.</summary>
    File,
}

public sealed class SourceConfig
{
    public SourceType Type { get; set; } = SourceType.Crawl;

    /// <summary>Folder of built HTML when <see cref="Type"/> is Folder. Relative to the config file.</summary>
    public string? Folder { get; set; }

    public FolderUrlStyle FolderUrlStyle { get; set; } = FolderUrlStyle.Clean;
}

public sealed class CrawlConfig
{
    public bool UseSitemap { get; set; } = true;

    /// <summary>Follow in-page links as well, not only when the sitemap is missing.</summary>
    public bool AlwaysFollowLinks { get; set; }

    public bool RespectRobotsTxt { get; set; } = true;

    public int MaxPages { get; set; } = 1000;

    /// <summary>Path globs such as /docs/** . Empty means everything.</summary>
    public List<string> Include { get; set; } = [];

    public List<string> Exclude { get; set; } = [];

    public int Concurrency { get; set; } = 4;

    public int DelayMilliseconds { get; set; }

    public int TimeoutSeconds { get; set; } = 30;

    public string UserAgent { get; set; } = "McpMySiteIndexer/0.1 (+https://github.com/pournasserian/mcp-my-site)";
}

public sealed class ChunkingConfig
{
    public int MaxChars { get; set; } = 1500;

    public int MinChars { get; set; } = 200;
}

public sealed class CacheConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>Cache file, relative to the config file.</summary>
    public string Path { get; set; } = ".mcpmysite/embedding-cache.sqlite";
}

public sealed class OutputConfig
{
    /// <summary>Index folder, relative to the config file.</summary>
    public string Directory { get; set; } = "index";

    public string Store { get; set; } = IndexFormat.JsonStoreType;
}

public sealed class SafetyConfig
{
    /// <summary>Fail when fewer pages than this were indexed.</summary>
    public int MinPages { get; set; } = 1;

    /// <summary>Fail when the page count fell by more than this percentage against the previous index in the output folder.</summary>
    public int MaxPageDropPercent { get; set; } = 50;

    /// <summary>Fail when more than this percentage of fetched pages errored.</summary>
    public int MaxErrorPercent { get; set; } = 20;

    /// <summary>Pages whose extracted text is shorter than this are flagged in the report.</summary>
    public int LowTextChars { get; set; } = 200;
}

public sealed class InstructionsConfig
{
    /// <summary>Optional text placed before the generated site outline in the MCP server instructions.</summary>
    public string? Preamble { get; set; }
}
