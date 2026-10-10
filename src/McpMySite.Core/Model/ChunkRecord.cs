namespace McpMySite.Core.Model;

/// <summary>
/// A heading-aware slice of a page. The embedded text is the page title plus the heading path
/// plus <see cref="Text"/>; only <see cref="Text"/> is stored.
/// </summary>
public sealed record ChunkRecord
{
    public required int Id { get; init; }

    public required int PageId { get; init; }

    /// <summary>Position of the chunk within its page, starting at 0.</summary>
    public required int Ordinal { get; init; }

    public IReadOnlyList<string> HeadingPath { get; init; } = [];

    public required string Text { get; init; }

    /// <summary>Builds the text that is embedded and keyword-indexed for a chunk.</summary>
    public static string BuildEmbeddingText(string pageTitle, IReadOnlyList<string> headingPath, string text)
    {
        var path = headingPath.Count == 0 ? pageTitle : $"{pageTitle} > {string.Join(" > ", headingPath)}";
        return $"{path}\n\n{text}";
    }
}
