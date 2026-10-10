using System.Text.Json;
using System.Text.Json.Serialization;
using McpMySite.Core;
using McpMySite.Core.Embeddings;

namespace McpMySite.Indexer.Configuration;

/// <summary>Contents of mcpmysite.json. Every run is a full rebuild driven by this file plus CLI overrides.</summary>
public sealed class IndexerConfig
{
    public const string DefaultFileName = "mcpmysite.json";

    /// <summary>Base URL of the site, e.g. https://example.com. Required.</summary>
    public string SiteUrl { get; set; } = string.Empty;

    /// <summary>Display name; falls back to og:site_name of the home page, then to the host.</summary>
    public string? SiteName { get; set; }

    public SourceConfig Source { get; set; } = new();

    public CrawlConfig Crawl { get; set; } = new();

    public ChunkingConfig Chunking { get; set; } = new();

    public EmbeddingOptions Embeddings { get; set; } = new();

    public CacheConfig Cache { get; set; } = new();

    public OutputConfig Output { get; set; } = new();

    public SafetyConfig Safety { get; set; } = new();

    /// <summary>Optional text placed before the generated site outline in the MCP server instructions.</summary>
    public string? InstructionsPreamble { get; set; }

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static IndexerConfig Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<IndexerConfig>(json, JsonOptions)
            ?? throw new InvalidDataException($"{path} is empty.");
    }

    public void Validate()
    {
        if (!Uri.TryCreate(SiteUrl, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            throw new InvalidOperationException("siteUrl must be an absolute http(s) URL.");
        }

        if (Source.Type == SourceType.Folder && string.IsNullOrWhiteSpace(Source.Folder))
        {
            throw new InvalidOperationException("source.folder is required when source.type is \"folder\".");
        }

        if (Output.Store is not (IndexFormat.JsonStoreType or IndexFormat.SqliteStoreType))
        {
            throw new InvalidOperationException("output.store must be \"json\" or \"sqlite\".");
        }

        if (Embeddings.Dimensions <= 0 || string.IsNullOrWhiteSpace(Embeddings.Model))
        {
            throw new InvalidOperationException("embeddings.model and embeddings.dimensions are required.");
        }
    }
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

    /// <summary>Folder of built HTML when <see cref="Type"/> is Folder.</summary>
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

    public string Path { get; set; } = ".mcpmysite/embedding-cache.sqlite";
}

public sealed class OutputConfig
{
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
