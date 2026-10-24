using McpMySite.Indexer.Build;
using McpMySite.Indexer.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace McpMySite.Tests.Indexer;

/// <summary>The signal profile and link graph of explore.json (#28): what the probe counts and what a build writes.</summary>
public class SignalProbeTests
{
    private static readonly Uri Site = new("https://example.test/");
    private static readonly DateTimeOffset BuiltAt = new(2026, 10, 24, 0, 0, 0, TimeSpan.Zero);

    private static PageDocument Page(int id, string url, string markdown, string language = "en", DateTimeOffset? modified = null, params string[] headings) => new()
    {
        Id = id, Url = url, Title = $"Page {id}", Language = language, Modified = modified, Headings = headings, Markdown = markdown,
    };

    [Fact]
    public void CountsMarkdownShapeOutsideCodeFences()
    {
        const string markdown = """
            # Guide

            See [the docs](https://example.test/docs) and [a mirror](https://EXAMPLE.test/docs/ "Docs"), or
            [elsewhere](https://other.example/page). Write to [us](mailto:hi@example.test) or jump [down](#here).
            A relative [page](/about) counts as internal. Use `dotnet build` then `dotnet test`.

            ![diagram](https://example.test/diagram.png)

            | Name | Value |
            | --- | --- |
            | a | 1 |

            - first
            - second
            1. third

            * * *

            A protocol-relative [link](//other.example/x) is external; [this one](//example.test/y) is internal.

            ```bash
            | --- | not a table |
            - not a list item
            `not a span`
            ```
            """;
        var pages = new[] { Page(1, "https://example.test/guide", markdown, headings: "Guide") };
        var chunks = new[] { new ChunkRecord { Id = 0, PageId = 1, Ordinal = 0, Text = "x" } };

        var bundle = SignalProbe.Profile(Site, pages, chunks, keywordOnly: false, new BuildReport(), BuiltAt);
        var profile = bundle.Profile;

        Assert.Equal(new Signal(1, 4), profile[SignalNames.InternalLinks]);
        Assert.Equal(new Signal(1, 2), profile[SignalNames.ExternalLinks]);
        Assert.Equal(new Signal(1, 1), profile[SignalNames.Images]);
        Assert.Equal(new Signal(1, 1), profile[SignalNames.Tables]);
        Assert.Equal(new Signal(1, 1), profile[SignalNames.CodeBlocks]);
        Assert.Equal(new Signal(1, 2), profile[SignalNames.CodeSpans]);
        Assert.Equal(new Signal(1, 3), profile[SignalNames.ListItems]);
        Assert.Equal(new Signal(1, 1), profile[SignalNames.Headings]);
        Assert.Equal(new Signal(1, 1), profile[SignalNames.Embeddings]);
        Assert.True(profile[SignalNames.Words].Count > 20);
    }

    [Fact]
    public void CountsPagesAndDistinctValuesForSiteSignals()
    {
        var modified = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
        var pages = new[]
        {
            Page(1, "https://example.test/", "Home."),
            Page(2, "https://example.test/docs/a", "A.", "en", modified),
            Page(3, "https://example.test/docs/b", "B.", "fr", modified),
            Page(4, "https://example.test/blog/c", "C.", "und"),
        };
        var chunks = new ChunkRecord[]
        {
            new() { Id = 0, PageId = 1, Ordinal = 0, Text = "Home." },
            new() { Id = 1, PageId = 2, Ordinal = 0, Text = "A." },
            new() { Id = 2, PageId = 2, Ordinal = 1, Text = "A2." },
        };
        var report = new BuildReport { ContentSources = { ["main"] = 3, ["auto"] = 1 } };
        report.LowTextPages.Add(new BuildReport.LowTextPage("https://example.test/", 5, "main"));

        var bundle = SignalProbe.Profile(Site, pages, chunks, keywordOnly: true, report, BuiltAt);
        var profile = bundle.Profile;

        Assert.Equal(ExploreBundle.CurrentFormatVersion, bundle.FormatVersion);
        Assert.Equal(4, bundle.PageCount);
        Assert.Equal(3, bundle.ChunkCount);
        Assert.Equal(new Signal(3, 2), profile[SignalNames.Sections]);
        Assert.Equal(new Signal(2, 2), profile[SignalNames.Dates]);
        Assert.Equal(new Signal(3, 2), profile[SignalNames.Languages]);
        Assert.Equal(new Signal(2, 3), profile[SignalNames.Chunks]);
        Assert.Equal(Signal.None, profile[SignalNames.Embeddings]);
        Assert.Equal(new Signal(1, 1), profile[SignalNames.ThinPages]);
        Assert.Equal(new Signal(1, 1), profile[SignalNames.AutoExtracted]);
        Assert.Equal(Signal.None, profile[SignalNames.Tables]);
        Assert.All(SignalNames.All, name => Assert.True(profile.ContainsKey(name), name));
    }

    [Fact]
    public void RoundTripsThroughJsonWithUnknownFieldsIgnored()
    {
        var bundle = SignalProbe.Profile(Site, [Page(1, "https://example.test/", "Home.")], [], keywordOnly: true, new BuildReport(), BuiltAt);

        var json = bundle.ToJson().Replace("\"profile\"", "\"futureField\": {\"x\": 1},\n  \"profile\"");
        var parsed = ExploreBundle.Parse(json);

        Assert.Contains("\"formatVersion\": 1", json);
        Assert.Equal(bundle.BuiltAt, parsed.BuiltAt);
        Assert.Equal(bundle.Profile[SignalNames.Words], parsed.Profile[SignalNames.Words]);
        Assert.Equal(new ExplorePage(1, "https://example.test/", "/", 1, 0, 0, 0), Assert.Single(parsed.Pages));
        Assert.Throws<InvalidDataException>(() => ExploreBundle.Parse(json.Replace("\"formatVersion\": 1", "\"formatVersion\": 2")));
    }

    [Fact]
    public void ParsesABundleWrittenBeforeTheLinkGraph()
    {
        const string json = """
            { "formatVersion": 1, "siteUrl": "https://example.test/", "builtAt": "2026-10-24T00:00:00+00:00", "pageCount": 0, "chunkCount": 0, "profile": {} }
            """;

        var parsed = ExploreBundle.Parse(json);

        Assert.Empty(parsed.Pages);
        Assert.Empty(parsed.Edges);
        Assert.Empty(parsed.Ghosts);
    }

    [Fact]
    public void ComputesClickDepthFromTheHomePage()
    {
        var pages = new[]
        {
            Page(1, "https://example.test/", "Start at [a](/docs/a)."),
            Page(2, "https://example.test/docs/a", "Then [b](/docs/b) and [back home](https://example.test/)."),
            Page(3, "https://example.test/docs/b", "The end."),
            Page(4, "https://example.test/blog/c", "Nobody links here, but it links to [a](/docs/a)."),
            Page(5, "https://example.test/blog/d", "An island."),
        };

        var bundle = SignalProbe.Profile(Site, pages, [], keywordOnly: true, new BuildReport(), BuiltAt);

        Assert.Equal(
        [
            new ExplorePage(1, "https://example.test/", "/", 3, 0, 1, 1),
            new ExplorePage(2, "https://example.test/docs/a", "/docs", 5, 1, 2, 2),
            new ExplorePage(3, "https://example.test/docs/b", "/docs", 2, 2, 1, 0),
            new ExplorePage(4, "https://example.test/blog/c", "/blog", 8, null, 0, 1),
            new ExplorePage(5, "https://example.test/blog/d", "/blog", 2, null, 0, 0),
        ], bundle.Pages);
        Assert.Equal([[1, 2], [2, 1], [2, 3], [4, 2]], bundle.Edges);
        Assert.Empty(bundle.Ghosts);
    }

    [Fact]
    public void DepthIsUnknownWhenTheHomePageIsNotIndexed()
    {
        var pages = new[]
        {
            Page(1, "https://example.test/docs/a", "See [b](/docs/b)."),
            Page(2, "https://example.test/docs/b", "B."),
        };

        var bundle = SignalProbe.Profile(Site, pages, [], keywordOnly: true, new BuildReport(), BuiltAt);

        Assert.All(bundle.Pages, p => Assert.Null(p.Depth));
        Assert.Equal([[1, 2]], bundle.Edges);
    }

    [Fact]
    public void ReportsInternalTargetsOutsideTheIndexAsGhosts()
    {
        var pages = new[]
        {
            Page(1, "https://example.test/", "A [gone page](/missing) and [its anchor](/missing#top), a [logo](/logo.png), a [sheet](/site.css), [mail](mailto:x@example.test)."),
            Page(2, "https://example.test/docs/a", "Also [gone](https://EXAMPLE.test/missing/) and a [draft](/docs/draft?v=2)."),
        };

        var bundle = SignalProbe.Profile(Site, pages, [], keywordOnly: true, new BuildReport(), BuiltAt);

        Assert.Equal([new Ghost("example.test/missing", 2), new Ghost("example.test/docs/draft?v=2", 1)], bundle.Ghosts);
        Assert.Empty(bundle.Edges);
        Assert.Equal(new Signal(2, 6), bundle.Profile[SignalNames.InternalLinks]);
    }

    [Fact]
    public void CountsRepeatedLinksOnceInTheGraph()
    {
        var pages = new[]
        {
            Page(1, "https://example.test/", "[a](/docs/a), [again](/docs/a/), [anchored](/docs/a#x), [absolute](https://Example.test/docs/a) and [myself](/)."),
            Page(2, "https://example.test/docs/a", "A."),
        };

        var bundle = SignalProbe.Profile(Site, pages, [], keywordOnly: true, new BuildReport(), BuiltAt);

        Assert.Equal([[1, 2]], bundle.Edges);
        Assert.Equal(new ExplorePage(1, "https://example.test/", "/", 6, 0, 0, 1), bundle.Pages[0]);
        Assert.Equal(new ExplorePage(2, "https://example.test/docs/a", "/docs", 1, 1, 1, 0), bundle.Pages[1]);
        Assert.Equal(new Signal(1, 5), bundle.Profile[SignalNames.InternalLinks]);
    }

    [Fact]
    public async Task BuildWritesTheBundleNextToTheIndex()
    {
        var folder = Path.Combine(Path.GetTempPath(), "mcpmysite-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "site", "docs"));
        await File.WriteAllTextAsync(Path.Combine(folder, "site", "index.html"),
            "<html><head><title>Acme</title></head><body><main><h1>Welcome</h1><p>Acme makes rockets and widgets for everyone, every day. Read the <a href=\"/docs/fuel\">fuel guide</a>.</p></main></body></html>");
        await File.WriteAllTextAsync(Path.Combine(folder, "site", "docs", "fuel.html"),
            "<html><head><title>Fuel</title></head><body><main><h1>Fuel</h1><p>Our rockets burn liquid oxygen and kerosene in every launch. See the <a href=\"/docs/engines\">engine notes</a>.</p><ul><li>Oxygen</li><li>Kerosene</li></ul></main></body></html>");

        var config = new IndexerConfig();
        config.Site.Url = "https://probe.test";
        config.Source.Type = SourceType.Folder;
        config.Source.Folder = "site";
        config.Cache.Enabled = false;
        config.Embeddings.Enabled = false;

        var report = await new IndexBuilder(NullLoggerFactory.Instance).RunAsync(config, folder, CancellationToken.None);
        Assert.True(report.Succeeded, report.FailureReason);

        var bundle = ExploreBundle.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "index", IndexFormat.ExploreFileName)));
        Assert.Equal("https://probe.test/", bundle.SiteUrl);
        Assert.Equal(2, bundle.PageCount);
        Assert.Equal(new Signal(2, 2), bundle.Profile[SignalNames.InternalLinks]);
        Assert.Equal(new Signal(1, 2), bundle.Profile[SignalNames.ListItems]);
        Assert.Equal(new Signal(1, 1), bundle.Profile[SignalNames.Sections]);
        Assert.Equal(Signal.None, bundle.Profile[SignalNames.Embeddings]);

        var home = Assert.Single(bundle.Pages, p => p.Url == "https://probe.test/");
        var fuel = Assert.Single(bundle.Pages, p => p.Url == "https://probe.test/docs/fuel");
        Assert.Equal(("/", 0, 0, 1), (home.Section, home.Depth, home.Inbound, home.Outbound));
        Assert.Equal(("/docs", 1, 1, 0), (fuel.Section, fuel.Depth, fuel.Inbound, fuel.Outbound));
        Assert.Equal([[home.Id, fuel.Id]], bundle.Edges);
        Assert.Equal([new Ghost("probe.test/docs/engines", 1)], bundle.Ghosts);

        using var store = await IndexStoreOpener.OpenAsync(Path.Combine(folder, "index"));
        Assert.Equal(bundle.BuiltAt, store.Manifest.BuiltAt);
    }
}
