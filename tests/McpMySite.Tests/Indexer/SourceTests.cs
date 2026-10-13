using McpMySite.Indexer.Build;
using McpMySite.Indexer.Configuration;
using McpMySite.Indexer.Sources;
using Microsoft.Extensions.Logging.Abstractions;

namespace McpMySite.Tests.Indexer;

public class GlobMatcherTests
{
    [Theory]
    [InlineData("/docs/**", "/docs/a/b", true)]
    [InlineData("/docs/**", "/blog/a", false)]
    [InlineData("/docs/*", "/docs/a", true)]
    [InlineData("/docs/*", "/docs/a/b", false)]
    [InlineData("docs", "/docs/intro", true)]
    [InlineData("docs", "/docsx", false)]
    [InlineData("/*.html", "/page.html", true)]
    public void MatchesGlobs(string glob, string path, bool expected)
    {
        Assert.Equal(expected, new GlobMatcher([glob], []).IsAllowed(path));
    }

    [Fact]
    public void ExcludeWinsOverInclude()
    {
        var matcher = new GlobMatcher(["/docs/**"], ["/docs/private/**"]);
        Assert.True(matcher.IsAllowed("/docs/public"));
        Assert.False(matcher.IsAllowed("/docs/private/key"));
    }

    [Fact]
    public void EmptyIncludeAllowsEverything()
    {
        Assert.True(new GlobMatcher([], ["/tag/**"]).IsAllowed("/anything"));
        Assert.False(new GlobMatcher([], ["/tag/**"]).IsAllowed("/tag/x"));
    }
}

public class RobotsTxtTests
{
    private const string Content = """
        # comment
        User-agent: *
        Disallow: /private/
        Allow: /private/public-note
        Disallow: /*.pdf$

        User-agent: McpMySiteIndexer
        Disallow: /drafts/

        Sitemap: https://acme.test/sitemap.xml
        Sitemap: https://acme.test/news.xml
        """;

    [Fact]
    public void PrefersGroupForOurAgent()
    {
        var robots = RobotsTxt.Parse(Content, "McpMySiteIndexer/0.1");
        Assert.False(robots.IsAllowed("/drafts/x"));
        Assert.True(robots.IsAllowed("/private/anything"));
        Assert.Equal(["https://acme.test/sitemap.xml", "https://acme.test/news.xml"], robots.Sitemaps);
    }

    [Fact]
    public void FallsBackToWildcardGroupWithLongestMatchWins()
    {
        var robots = RobotsTxt.Parse(Content, "OtherBot");
        Assert.False(robots.IsAllowed("/private/x"));
        Assert.True(robots.IsAllowed("/private/public-note"));
        Assert.False(robots.IsAllowed("/files/a.pdf"));
        Assert.True(robots.IsAllowed("/files/a.pdfx"));
        Assert.True(robots.IsAllowed("/"));
    }
}

public class FolderSourceTests
{
    [Theory]
    [InlineData(FolderUrlStyle.Clean, "index.html", "https://acme.test/")]
    [InlineData(FolderUrlStyle.Clean, "about.html", "https://acme.test/about")]
    [InlineData(FolderUrlStyle.Clean, "docs/index.html", "https://acme.test/docs/")]
    [InlineData(FolderUrlStyle.Clean, "docs/intro.html", "https://acme.test/docs/intro")]
    [InlineData(FolderUrlStyle.File, "docs/intro.html", "https://acme.test/docs/intro.html")]
    public void MapsFilesToUrls(FolderUrlStyle style, string relativeFile, string expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "site");
        var source = new FolderSource(root, new Uri("https://acme.test"), style, new GlobMatcher([], []), 10);
        Assert.Equal(expected, source.ToUrl(root, Path.Combine(root, relativeFile.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Fact]
    public async Task ReadsHtmlFilesAndSkipsOthers()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcpmysite-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "docs"));
        await File.WriteAllTextAsync(Path.Combine(root, "index.html"), "<html><body>home</body></html>");
        await File.WriteAllTextAsync(Path.Combine(root, "docs", "a.html"), "<html><body>a</body></html>");
        await File.WriteAllTextAsync(Path.Combine(root, "style.css"), "body{}");

        var source = new FolderSource(root, new Uri("https://acme.test"), FolderUrlStyle.Clean, new GlobMatcher([], []), 10);
        var pages = await source.GetPagesAsync(CancellationToken.None).ToListAsync();

        Assert.Equal(["https://acme.test/docs/a", "https://acme.test/"], pages.Select(p => p.Url));
        Assert.Empty(source.Errors);
    }
}

public class CrawlSourceTests
{
    private static CrawlSource Create(GlobMatcher? filter = null) =>
        new(new HttpClient(), new CrawlConfig(), new Uri("https://acme.test"), filter ?? new GlobMatcher([], []), NullLogger.Instance);

    [Theory]
    [InlineData("https://acme.test/docs#top", true, "https://acme.test/docs")]
    [InlineData("https://www.acme.test/docs", true, "https://acme.test/docs")]
    [InlineData("http://acme.test/docs", true, "https://acme.test/docs")]
    [InlineData("https://other.test/docs", false, "")]
    [InlineData("https://acme.test/file.pdf", false, "")]
    [InlineData("mailto:a@acme.test", false, "")]
    public void NormalizesCandidateUrls(string href, bool expected, string normalized)
    {
        var ok = Create().TryNormalize(href, out var result);
        Assert.Equal(expected, ok);
        if (expected)
        {
            Assert.Equal(normalized, result);
        }
    }

    [Fact]
    public void ExtractsSameHostLinksWithoutQueryStrings()
    {
        var html = """<a href="/a">A</a> <a href='b/c'>B</a> <a href="https://acme.test/d?x=1">D</a> <a href="https://elsewhere.test/">E</a> <a href="#frag">F</a> <a href="/tag/x">T</a>""";
        var links = Create(new GlobMatcher([], ["/tag/**"])).ExtractLinks(html, "https://acme.test/docs/").ToList();
        Assert.Equal(["https://acme.test/a", "https://acme.test/docs/b/c"], links);
    }
}

public class SiteOutlineTests
{
    private static readonly DateTimeOffset BuiltAt = new(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DescribesSiteToolsAndSections()
    {
        var outline = SiteOutline.Build("Example", "https://example.test/", "Acme makes\n rockets.", TestIndex.Pages, BuiltAt, "Be concise.");

        Assert.StartsWith("Be concise.\n\nExample (https://example.test/): Acme makes rockets.\n", outline);
        Assert.Contains("3 pages, a snapshot taken on 2026-10-10", outline);
        Assert.Contains("`search`", outline);
        Assert.Contains("`fetch`", outline);
        Assert.Contains("Cite the URL", outline);
        Assert.Contains("- /docs (2 pages): Rockets; Widgets", outline);
        Assert.DoesNotContain("- / ", outline);
        Assert.Contains("Content language: en, fr.", outline);
        Assert.DoesNotContain("\r", outline);
    }

    [Fact]
    public void WithoutDescriptionStartsWithNameAndUrl()
    {
        var outline = SiteOutline.Build("Example", "https://example.test/", null, TestIndex.Pages, BuiltAt, null);
        Assert.StartsWith("Example (https://example.test/).\n\n", outline);
    }

    [Fact]
    public void LabelsSectionsWithTheirLandingPage()
    {
        PageDocument[] pages =
        [
            .. TestIndex.Pages,
            new() { Id = 4, Url = "https://example.test/docs/", Title = "Documentation", Markdown = "All the docs." },
            new() { Id = 5, Url = "https://example.test/about", Title = "About us", Markdown = "Who we are." },
        ];

        var outline = SiteOutline.Build("Example", "https://example.test/", null, pages, BuiltAt, null);

        Assert.Contains("- Documentation (/docs, 3 pages): Rockets; Widgets\n", outline);
        Assert.EndsWith("- About us (/about, 1 page)", outline);
    }
}

public class IndexStatsTests
{
    [Fact]
    public void SummarisesRunAndPages()
    {
        var report = new BuildReport { PagesFetched = 5, PagesSkippedNoIndex = 1, PagesSkippedEmpty = 1, EmbeddingsGenerated = 3, EmbeddingsFromCache = 1 };
        report.Errors.Add("https://example.test/broken: 500");

        var stats = IndexBuilder.BuildStats(report, SourceType.Crawl, TestIndex.Pages, TimeSpan.FromSeconds(12.34));

        Assert.Equal("crawl", stats.Source);
        Assert.Equal(12.3, stats.DurationSeconds);
        Assert.Equal(5, stats.PagesFetched);
        Assert.Equal(1, stats.Errors);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), stats.NewestPageModified);
        Assert.Equal(new Dictionary<string, int> { ["en"] = 2, ["fr"] = 1 }, stats.Languages);
        Assert.Equal([new SectionStats("/docs", null, 2), new SectionStats("/", "Home", 1)], stats.Sections);
        Assert.True(stats.Words > 20);
    }

    [Fact]
    public void ManifestRoundTripsStatsAndStillReadsOlderManifests()
    {
        var manifest = TestIndex.Manifest(IndexFormat.JsonStoreType) with
        {
            SiteDescription = "Rockets and widgets.",
            Stats = new IndexStats { PagesFetched = 3, Sections = [new SectionStats("/docs", "Docs", 2)] },
        };

        var parsed = IndexManifest.Parse(manifest.ToJson());
        Assert.Equal("Rockets and widgets.", parsed.SiteDescription);
        Assert.Equal(3, parsed.Stats!.PagesFetched);
        Assert.Equal("Docs", parsed.Stats.Sections[0].Title);

        var older = IndexManifest.Parse(TestIndex.Manifest(IndexFormat.JsonStoreType).ToJson());
        Assert.Null(older.Stats);
        Assert.Null(older.SiteDescription);
    }
}
