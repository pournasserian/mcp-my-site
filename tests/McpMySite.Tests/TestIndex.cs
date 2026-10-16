namespace McpMySite.Tests;

/// <summary>Builds a small deterministic index on disk for store and server tests.</summary>
public static class TestIndex
{
    public const int Dimensions = 4;

    public static readonly PageDocument[] Pages =
    [
        new()
        {
            Id = 1, Url = "https://example.test/", Title = "Home", Description = "Welcome page", Language = "en",
            Headings = ["Welcome"], Markdown = "# Welcome\n\nAcme makes rockets and widgets for everyone.",
        },
        new()
        {
            Id = 2, Url = "https://example.test/docs/rockets", Title = "Rockets", Language = "en",
            Headings = ["Rockets", "Fuel"], Modified = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero),
            Markdown = "# Rockets\n\nOur rockets fly high.\n\n## Fuel\n\nRockets burn liquid oxygen and kerosene.",
        },
        new()
        {
            Id = 3, Url = "https://example.test/docs/widgets", Title = "Widgets", Language = "fr",
            Headings = ["Widgets"], Markdown = "# Widgets\n\nWidgets are small gadgets with gears.",
        },
    ];

    public static readonly ChunkRecord[] Chunks =
    [
        new() { Id = 0, PageId = 1, Ordinal = 0, HeadingPath = [], Text = "Acme makes rockets and widgets for everyone." },
        new() { Id = 1, PageId = 2, Ordinal = 0, HeadingPath = [], Text = "Our rockets fly high." },
        new() { Id = 2, PageId = 2, Ordinal = 1, HeadingPath = ["Fuel"], Text = "Rockets burn liquid oxygen and kerosene." },
        new() { Id = 3, PageId = 3, Ordinal = 0, HeadingPath = [], Text = "Widgets are small gadgets with gears." },
    ];

    /// <summary>One vector per chunk; chunk 2 (fuel) points along the first axis, chunk 3 (widgets) along the second.</summary>
    public static readonly float[] Vectors =
    [
        0.5f, 0.5f, 0.0f, 0.0f,
        0.8f, 0.1f, 0.1f, 0.0f,
        1.0f, 0.0f, 0.0f, 0.0f,
        0.0f, 1.0f, 0.0f, 0.0f,
    ];

    public static readonly float[] FuelQuery = [1.0f, 0.0f, 0.0f, 0.0f];

    public static readonly float[] WidgetQuery = [0.0f, 1.0f, 0.0f, 0.0f];

    /// <param name="keywordOnly">Describes an index without vectors, as the indexer writes with Embeddings:Enabled false.</param>
    public static IndexManifest Manifest(string storeType, DateTimeOffset? builtAt = null, bool keywordOnly = false) => new()
    {
        FormatVersion = IndexFormat.CurrentVersion,
        StoreType = storeType,
        SiteUrl = "https://example.test/",
        SiteName = "Example",
        KeywordOnly = keywordOnly,
        EmbeddingModel = keywordOnly ? null : "test-model",
        EmbeddingDimensions = keywordOnly ? 0 : Dimensions,
        BuiltAt = builtAt ?? new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero),
        PageCount = Pages.Length,
        ChunkCount = Chunks.Length,
        Instructions = "Test instructions.",
    };

    public static async Task<string> WriteAsync(string storeType, DateTimeOffset? builtAt = null, bool keywordOnly = false)
    {
        var directory = Path.Combine(Path.GetTempPath(), "mcpmysite-tests", Guid.NewGuid().ToString("N"));
        await IndexStoreOpener.CreateWriter(storeType).WriteAsync(directory, Manifest(storeType, builtAt, keywordOnly), Pages, Chunks, keywordOnly ? [] : Vectors);
        return directory;
    }
}
