using McpMySite.Indexer.Build;
using McpMySite.Indexer.Configuration;
using McpMySite.Indexer.Extraction;
using McpMySite.Indexer.Sources;
using Microsoft.Extensions.Logging.Abstractions;

namespace McpMySite.Tests.Indexer;

/// <summary>The page store: conditional requests, 304 replays and sitemap lastmod skips (#51).</summary>
public class SkipUnchangedTests
{
    private const string Body = "Acme makes rockets and widgets for everyone, every day of the week.";
    private static readonly DateTimeOffset Lastmod = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly string Date = "Wed, 01 Oct 2026 12:00:00 GMT";

    private static string NewFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "mcpmysite-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static PageStore.Entry Stored(string url, string? etag, string? lastModified, DateTimeOffset? lastmod = null, DateTimeOffset? fetchedAt = null, IReadOnlyList<string>? links = null) =>
        new(url, etag, lastModified, lastmod, Extracted(url), links ?? [], fetchedAt ?? DateTimeOffset.UtcNow);

    private static ExtractedPage Extracted(string url) => new()
    {
        Url = url,
        Title = "Stored",
        Language = "en",
        Headings = ["Stored"],
        Markdown = "# Stored\n\nStored text.",
        TextLength = 12,
        NoIndex = false,
        ContentSource = "main",
    };

    private static async Task<List<RawPage>> CrawlAsync(FakeSite site, PageStore? store, CrawlConfig? config = null)
    {
        using var http = new HttpClient();
        var source = new CrawlSource(http, config ?? new CrawlConfig(), new Uri(site.Url), new GlobMatcher([], []), NullLogger.Instance, store);
        var pages = await source.GetPagesAsync(CancellationToken.None).ToListAsync();
        Assert.Empty(source.Errors);
        return pages;
    }

    [Fact]
    public async Task SendsConditionalHeadersOnlyForStoredValidators()
    {
        await using var site = await FakeSite.StartAsync();
        site.Pages["/a"] = new FakeSite.Page(FakeSite.Html("A", Body), "\"a1\"", Date);
        site.Pages["/b"] = new FakeSite.Page(FakeSite.Html("B", Body), "\"b1\"", Date);
        site.Pages["/c"] = new FakeSite.Page(FakeSite.Html("C", Body));
        using var store = new PageStore(Path.Combine(NewFolder(), PageStore.FileName), new Uri(site.Url));
        store.Put(Stored(site.Url + "a", "\"a1\"", Date));
        store.Put(Stored(site.Url + "b", "\"old\"", null));

        var pages = await CrawlAsync(site, store);

        var requests = site.Requests.ToDictionary(r => r.Path);
        Assert.Equal(("\"a1\"", Date), (requests["/a"].IfNoneMatch, requests["/a"].IfModifiedSince));
        Assert.Equal(("\"old\"", null), (requests["/b"].IfNoneMatch, requests["/b"].IfModifiedSince));
        Assert.Equal((null, null), (requests["/c"].IfNoneMatch, requests["/c"].IfModifiedSince));

        var byUrl = pages.ToDictionary(p => p.Url);
        Assert.NotNull(byUrl[site.Url + "a"].Replayed);
        Assert.Equal(string.Empty, byUrl[site.Url + "a"].Html);
        Assert.Null(byUrl[site.Url + "b"].Replayed);
        Assert.Equal(("\"b1\"", Date), (byUrl[site.Url + "b"].ETag, byUrl[site.Url + "b"].LastModified));
        Assert.Null(byUrl[site.Url + "c"].ETag);
        Assert.Contains("<h1>C</h1>", byUrl[site.Url + "c"].Html);
    }

    [Fact]
    public async Task WeakETagRoundTrips()
    {
        await using var site = await FakeSite.StartAsync();
        site.Pages["/a"] = new FakeSite.Page(FakeSite.Html("A", Body), "W/\"gzip-a1\"");
        using var store = new PageStore(Path.Combine(NewFolder(), PageStore.FileName), new Uri(site.Url));

        var first = await CrawlAsync(site, store);
        Assert.Equal("W/\"gzip-a1\"", first.Single().ETag);
        store.Put(Stored(site.Url + "a", first.Single().ETag, null));

        var second = await CrawlAsync(site, store);
        Assert.Equal("W/\"gzip-a1\"", site.Requests.Last(r => r.Path == "/a").IfNoneMatch);
        Assert.NotNull(second.Single().Replayed);
    }

    [Fact]
    public async Task EqualSitemapLastmodSkipsTheRequest()
    {
        await using var site = await FakeSite.StartAsync();
        site.Pages["/same"] = new FakeSite.Page(FakeSite.Html("Same", Body), "\"s1\"", Lastmod: Lastmod);
        site.Pages["/changed"] = new FakeSite.Page(FakeSite.Html("Changed", Body), "\"c1\"", Lastmod: Lastmod.AddDays(1));
        site.Pages["/nolastmod"] = new FakeSite.Page(FakeSite.Html("None", Body), "\"n1\"");
        using var store = new PageStore(Path.Combine(NewFolder(), PageStore.FileName), new Uri(site.Url));
        store.Put(Stored(site.Url + "same", "\"s1\"", null, Lastmod));
        store.Put(Stored(site.Url + "changed", "\"c1\"", null, Lastmod));
        store.Put(Stored(site.Url + "nolastmod", "\"n1\"", null, Lastmod));

        var pages = await CrawlAsync(site, store);

        Assert.Equal(["/changed", "/nolastmod"], site.PageRequests.Order().ToList());
        Assert.All(pages, p => Assert.NotNull(p.Replayed));
        Assert.Equal(Lastmod.AddDays(1), store.Get(site.Url + "changed")!.SitemapLastmod);
        Assert.Equal(Lastmod, pages.Single(p => p.Url == site.Url + "same").LastModifiedHint);
    }

    [Fact]
    public async Task FullRefreshDaysForcesAnUnconditionalFetch()
    {
        await using var site = await FakeSite.StartAsync();
        site.Pages["/a"] = new FakeSite.Page(FakeSite.Html("A", Body), "\"a1\"", Date, Lastmod);
        using var store = new PageStore(Path.Combine(NewFolder(), PageStore.FileName), new Uri(site.Url));
        store.Put(Stored(site.Url + "a", "\"a1\"", Date, Lastmod, DateTimeOffset.UtcNow.AddDays(-10)));

        var pages = await CrawlAsync(site, store, new CrawlConfig { FullRefreshDays = 7 });

        var request = Assert.Single(site.Requests, r => r.Path == "/a");
        Assert.Null(request.IfNoneMatch);
        Assert.Null(pages.Single().Replayed);

        site.ClearRequests();
        var kept = await CrawlAsync(site, store, new CrawlConfig { FullRefreshDays = 0 });
        Assert.Empty(site.PageRequests);
        Assert.NotNull(kept.Single().Replayed);
    }

    [Fact]
    public async Task StoreRoundTripsExtractionAndLinks()
    {
        using var store = new PageStore(Path.Combine(NewFolder(), PageStore.FileName), new Uri("https://acme.test"));
        var page = Extracted("https://acme.test/a") with { Description = "Desc", Modified = Lastmod, NoIndex = true, SiteName = "Acme" };
        var fetchedAt = new DateTimeOffset(2026, 10, 20, 8, 30, 0, TimeSpan.Zero);
        store.Put(new PageStore.Entry("https://acme.test/a", "W/\"x\"", Date, Lastmod, page, ["https://acme.test/b"], fetchedAt));

        var entry = store.Get("https://acme.test/a");

        Assert.NotNull(entry);
        Assert.Equal(("W/\"x\"", Date, Lastmod, fetchedAt), (entry.ETag, entry.LastModified, entry.SitemapLastmod, entry.FetchedAt));
        Assert.Equal(page, entry.Page with { Headings = page.Headings });
        Assert.Equal(["Stored"], entry.Page.Headings);
        Assert.Equal(["https://acme.test/b"], entry.Links);
        Assert.Null(store.Get("https://acme.test/missing"));
    }

    private static IndexerConfig Config(FakeSite site, string folder)
    {
        var config = new IndexerConfig();
        config.Site.Url = site.Url;
        config.Embeddings.Enabled = false;
        // One request at a time keeps the page order, so two builds of the same site write the same files.
        config.Crawl.Concurrency = 1;
        config.Cache.Path = Path.Combine(folder, ".mcpmysite", "embedding-cache.sqlite");
        config.Output.Directory = Path.Combine(folder, "index");
        return config;
    }

    private static async Task<BuildReport> BuildAsync(IndexerConfig config, string folder)
    {
        var report = await new IndexBuilder(NullLoggerFactory.Instance).RunAsync(config, folder, CancellationToken.None);
        Assert.True(report.Succeeded, report.FailureReason);
        return report;
    }

    [Fact]
    public async Task SecondBuildOfAnUnchangedSiteDownloadsNoPages()
    {
        await using var site = await FakeSite.StartAsync();
        site.Pages["/"] = new FakeSite.Page(FakeSite.Html("Home", Body), "\"h1\"", Date, Lastmod);
        site.Pages["/docs"] = new FakeSite.Page(FakeSite.Html("Docs", Body), "\"d1\"", Date, Lastmod);
        site.Pages["/etag-only"] = new FakeSite.Page(FakeSite.Html("Tagged", Body), "\"e1\"");
        site.Pages["/hidden"] = new FakeSite.Page(FakeSite.Html("Hidden", Body, noIndex: true), "\"x1\"", Lastmod: Lastmod);
        var folder = NewFolder();
        var config = Config(site, folder);

        var first = await BuildAsync(config, folder);
        Assert.Equal(3, first.PagesIndexed);
        Assert.Equal(1, first.PagesSkippedNoIndex);
        Assert.Equal(0, first.PagesNotModified + first.PagesSkippedByLastmod);
        Assert.True(File.Exists(Path.Combine(folder, ".mcpmysite", PageStore.FileName)));
        var pagesBefore = await File.ReadAllTextAsync(Path.Combine(folder, "index", IndexFormat.PagesFileName));
        var chunksBefore = await File.ReadAllTextAsync(Path.Combine(folder, "index", IndexFormat.ChunksFileName));

        site.ClearRequests();
        var second = await BuildAsync(config, folder);

        Assert.Equal(["/robots.txt", "/sitemap.xml", "/etag-only"], site.Requests.Select(r => r.Path).ToList());
        Assert.Equal(3, second.PagesIndexed);
        Assert.Equal(4, second.PagesFetched);
        Assert.Equal(1, second.PagesNotModified);
        Assert.Equal(3, second.PagesSkippedByLastmod);
        Assert.Equal(1, second.PagesSkippedNoIndex);
        Assert.Equal(pagesBefore, await File.ReadAllTextAsync(Path.Combine(folder, "index", IndexFormat.PagesFileName)));
        Assert.Equal(chunksBefore, await File.ReadAllTextAsync(Path.Combine(folder, "index", IndexFormat.ChunksFileName)));
    }

    [Fact]
    public async Task ChangedPageIsFetchedAndItsValidatorsStored()
    {
        await using var site = await FakeSite.StartAsync();
        site.Pages["/a"] = new FakeSite.Page(FakeSite.Html("A", Body), "\"a1\"", Date, Lastmod);
        var folder = NewFolder();
        var config = Config(site, folder);
        await BuildAsync(config, folder);

        site.Pages["/a"] = new FakeSite.Page(FakeSite.Html("A2", Body + " Now with more."), "\"a2\"", "Thu, 02 Oct 2026 12:00:00 GMT", Lastmod.AddDays(1));
        site.ClearRequests();
        var report = await BuildAsync(config, folder);

        var request = Assert.Single(site.Requests, r => r.Path == "/a");
        Assert.Equal(("\"a1\"", Date), (request.IfNoneMatch, request.IfModifiedSince));
        Assert.Equal(0, report.PagesNotModified + report.PagesSkippedByLastmod);
        using var store = new PageStore(Path.Combine(folder, ".mcpmysite", PageStore.FileName), new Uri(site.Url));
        var entry = store.Get(site.Url + "a")!;
        Assert.Equal(("\"a2\"", "Thu, 02 Oct 2026 12:00:00 GMT", Lastmod.AddDays(1), "A2"), (entry.ETag, entry.LastModified, entry.SitemapLastmod, entry.Page.Title));
    }

    [Fact]
    public async Task SkipUnchangedOffFetchesEverythingUnconditionally()
    {
        await using var site = await FakeSite.StartAsync();
        site.Pages["/a"] = new FakeSite.Page(FakeSite.Html("A", Body), "\"a1\"", Date, Lastmod);
        var folder = NewFolder();
        var config = Config(site, folder);
        await BuildAsync(config, folder);

        config.Crawl.SkipUnchanged = false;
        site.ClearRequests();
        var report = await BuildAsync(config, folder);

        var request = Assert.Single(site.Requests, r => r.Path == "/a");
        Assert.Null(request.IfNoneMatch);
        Assert.Equal(0, report.PagesNotModified + report.PagesSkippedByLastmod);
        Assert.Equal(1, report.PagesIndexed);
    }

    [Fact]
    public async Task LinkFollowingFindsPagesLinkedOnlyFromReplayedPages()
    {
        await using var site = await FakeSite.StartAsync();
        site.Sitemap = false;
        site.Pages["/"] = new FakeSite.Page(FakeSite.Html("Home", Body + " <a href=\"/a\">A</a>"), "\"h1\"");
        site.Pages["/a"] = new FakeSite.Page(FakeSite.Html("A", Body + " <a href=\"/b\">B</a>"), "\"a1\"");
        site.Pages["/b"] = new FakeSite.Page(FakeSite.Html("B", Body), "\"b1\"");
        var folder = NewFolder();
        var config = Config(site, folder);
        var first = await BuildAsync(config, folder);
        Assert.Equal(3, first.PagesIndexed);

        site.ClearRequests();
        var second = await BuildAsync(config, folder);

        Assert.Equal(3, second.PagesIndexed);
        Assert.Equal(3, second.PagesNotModified);
        Assert.Equal(["/", "/a", "/b"], site.PageRequests.Order().ToList());
    }
}
