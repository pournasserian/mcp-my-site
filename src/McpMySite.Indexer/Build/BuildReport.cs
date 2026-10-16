using System.Text.Json;
using System.Text.Json.Serialization;

namespace McpMySite.Indexer.Build;

/// <summary>Summary of one indexer run, written next to the index as report.json.</summary>
public sealed class BuildReport
{
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? FinishedAt { get; set; }

    public bool Succeeded { get; set; }

    public string? FailureReason { get; set; }

    public string SiteUrl { get; set; } = string.Empty;

    public string Store { get; set; } = string.Empty;

    public bool KeywordOnly { get; set; }

    public int PagesFetched { get; set; }

    public int PagesIndexed { get; set; }

    public int PagesSkippedNoIndex { get; set; }

    public int PagesSkippedDuplicate { get; set; }

    public int PagesSkippedEmpty { get; set; }

    public int Chunks { get; set; }

    public int EmbeddingsGenerated { get; set; }

    public int EmbeddingsFromCache { get; set; }

    public int PreviousPageCount { get; set; }

    public List<string> Errors { get; set; } = [];

    /// <summary>Pages where little text was extracted; worth a manual look.</summary>
    public List<LowTextPage> LowTextPages { get; set; } = [];

    public Dictionary<string, int> ContentSources { get; set; } = [];

    public sealed record LowTextPage(string Url, int TextLength, string ContentSource);

    public double ErrorPercent => PagesFetched + Errors.Count == 0 ? 0 : 100.0 * Errors.Count / (PagesFetched + Errors.Count);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public Task SaveAsync(string path, CancellationToken cancellationToken) =>
        File.WriteAllTextAsync(path, JsonSerializer.Serialize(this, JsonOptions), cancellationToken);
}
