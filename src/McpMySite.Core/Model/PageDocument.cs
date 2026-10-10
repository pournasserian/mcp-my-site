namespace McpMySite.Core.Model;

/// <summary>One indexed page. Content is stored as Markdown; no HTML is kept.</summary>
public sealed record PageDocument
{
    public required int Id { get; init; }

    public required string Url { get; init; }

    public required string Title { get; init; }

    public string? Description { get; init; }

    /// <summary>BCP-47 language tag, or "und" when unknown.</summary>
    public string Language { get; init; } = "und";

    public IReadOnlyList<string> Headings { get; init; } = [];

    public DateTimeOffset? Modified { get; init; }

    public required string Markdown { get; init; }
}
