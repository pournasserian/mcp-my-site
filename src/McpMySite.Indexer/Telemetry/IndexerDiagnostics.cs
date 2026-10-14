using McpMySite.Indexer.Build;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace McpMySite.Indexer.Telemetry;

/// <summary>The indexer's own ActivitySource and Meter, and the instruments recorded on them.</summary>
public static class IndexerDiagnostics
{
    /// <summary>Name of both the ActivitySource and the Meter.</summary>
    public const string Name = "McpMySite.Indexer";

    public static readonly ActivitySource ActivitySource = new(Name);

    public static readonly Meter Meter = new(Name);

    private static readonly Histogram<double> BuildDuration = Meter.CreateHistogram<double>(
        "mcpmysite.build.duration", unit: "s", description: "Duration of an index build, by result.");

    private static readonly Counter<long> BuildPages = Meter.CreateCounter<long>(
        "mcpmysite.build.pages", description: "Pages seen by a build, by outcome: indexed, noindex, duplicate, empty or failed.");

    private static readonly Counter<long> BuildChunks = Meter.CreateCounter<long>(
        "mcpmysite.build.chunks", description: "Chunks written by a build.");

    private static readonly Counter<long> BuildEmbeddings = Meter.CreateCounter<long>(
        "mcpmysite.build.embeddings", description: "Chunk embeddings by origin: generated or cache.");

    /// <summary>Records one finished build. Every instrument carries the site, source, store and result.</summary>
    public static void RecordBuild(BuildReport report, string source, TimeSpan elapsed)
    {
        var tags = new TagList
        {
            { "site", new Uri(report.SiteUrl).Host },
            { "source", source },
            { "store", report.Store },
            { "result", report.Succeeded ? "succeeded" : "failed" },
        };

        BuildDuration.Record(elapsed.TotalSeconds, tags);
        AddPages(report.PagesIndexed, "indexed");
        AddPages(report.PagesSkippedNoIndex, "noindex");
        AddPages(report.PagesSkippedDuplicate, "duplicate");
        AddPages(report.PagesSkippedEmpty, "empty");
        AddPages(report.Errors.Count, "failed");
        BuildChunks.Add(report.Chunks, tags);
        BuildEmbeddings.Add(report.EmbeddingsGenerated, With(tags, "origin", "generated"));
        BuildEmbeddings.Add(report.EmbeddingsFromCache, With(tags, "origin", "cache"));

        void AddPages(int count, string outcome) => BuildPages.Add(count, With(tags, "outcome", outcome));
    }

    private static TagList With(TagList tags, string key, string value)
    {
        var copy = tags;
        copy.Add(key, value);
        return copy;
    }
}
