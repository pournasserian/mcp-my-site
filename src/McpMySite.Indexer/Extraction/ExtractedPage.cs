namespace McpMySite.Indexer.Extraction;

/// <summary>Result of turning one HTML document into Markdown plus metadata.</summary>
public sealed record ExtractedPage
{
    public required string Url { get; init; }

    public required string Title { get; init; }

    public string? Description { get; init; }

    public required string Language { get; init; }

    public required IReadOnlyList<string> Headings { get; init; }

    public DateTimeOffset? Modified { get; init; }

    public required string Markdown { get; init; }

    /// <summary>Length of the flattened text, used to flag thin pages.</summary>
    public required int TextLength { get; init; }

    public required bool NoIndex { get; init; }

    public string? SiteName { get; init; }

    /// <summary>Which strategy picked the content: "main", "article" or "auto".</summary>
    public required string ContentSource { get; init; }
}
