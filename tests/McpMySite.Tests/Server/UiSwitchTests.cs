extern alias server;
using McpMySite.Server.Api;
using McpMySite.Server.Options;
using McpMySite.Server.Pages;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace McpMySite.Tests.Server;

// The finer Ui switches: each hides one part of the pages, and what is hidden there is also left out of the other routes.
public class UiSwitchTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    // UseSetting, not ConfigureAppConfiguration: Program reads which endpoints to map before the app is built.
    private WebApplicationFactory<server::Program> With(params (string Key, string Value)[] settings) =>
        fixture.Factory.WithWebHostBuilder(builder =>
        {
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }
        });

    private static async Task<string> WaitForIndexAsync(HttpClient client, string path = "/")
    {
        for (var i = 0; i < 100; i++)
        {
            var response = await client.GetAsync(path);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadAsStringAsync();
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("Index did not load.");
    }

    [Fact]
    public async Task LandingPageOffKeepsTheOtherPagesAndStopsTheBrowserRedirect()
    {
        using var factory = With(("Ui:LandingPage", "false"));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var home = await client.GetAsync("/");
        Assert.Equal("text/plain", home.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/playground")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/docs/")).StatusCode);
        Assert.StartsWith("# Example", await WaitForIndexAsync(client, "/llms.txt"));

        var sitemap = await client.GetStringAsync("/sitemap.xml");
        Assert.DoesNotContain("<loc>http://localhost/</loc>", sitemap);
        Assert.Contains("<loc>http://localhost/docs/</loc>", sitemap);

        var browser = new HttpRequestMessage(HttpMethod.Get, "/mcp");
        browser.Headers.Accept.ParseAdd("text/html");
        Assert.NotEqual(HttpStatusCode.Found, (await client.SendAsync(browser)).StatusCode);
    }

    [Fact]
    public async Task ApiReferenceOffDropsDocsButKeepsTheOpenApiDocument()
    {
        using var factory = With(("Ui:ApiReference", "false"));
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/docs/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/openapi/v1.json")).StatusCode);

        var html = await WaitForIndexAsync(client);
        Assert.DoesNotContain("API reference", html);
        Assert.Contains("http://localhost/openapi/v1.json", html);
        Assert.DoesNotContain("/docs/", await client.GetStringAsync("/sitemap.xml"));

        var info = await client.GetFromJsonAsync<JsonElement>("/api/info");
        Assert.Equal(JsonValueKind.Null, info.GetProperty("endpoints").GetProperty("docs").ValueKind);
    }

    [Fact]
    public async Task SearchApiCanBeLeftOutOfEveryPageWhileStillAnswering()
    {
        using var factory = With(("Ui:ShowSearchApi", "false"));
        using var client = factory.CreateClient();

        var html = await WaitForIndexAsync(client);
        Assert.DoesNotContain("id=\"api\"", html);
        Assert.DoesNotContain("/api/search", html);
        Assert.DoesNotContain("/openapi/v1.json", html);
        Assert.DoesNotContain("scripts through", await client.GetStringAsync("/llms.txt"));

        var info = await client.GetFromJsonAsync<JsonElement>("/api/info");
        Assert.Equal(JsonValueKind.Null, info.GetProperty("endpoints").GetProperty("searchApi").ValueKind);
        Assert.Equal(JsonValueKind.Null, info.GetProperty("endpoints").GetProperty("openApi").ValueKind);

        // Hiding is not access control.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/search?q=rockets")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/openapi/v1.json")).StatusCode);
    }

    [Fact]
    public async Task SearchDetailsOffHidesHowSearchWorks()
    {
        using var factory = With(("Ui:ShowSearchDetails", "false"));
        using var client = factory.CreateClient();

        var html = await WaitForIndexAsync(client);
        Assert.DoesNotContain("<dt>Embedding model</dt>", html);
        Assert.DoesNotContain("<dt>Search</dt>", html);
        Assert.DoesNotContain("keyword-only", html);
        Assert.Contains("<section id=\"build\"", html);

        var index = (await client.GetFromJsonAsync<JsonElement>("/api/info")).GetProperty("index");
        Assert.Equal(JsonValueKind.Null, index.GetProperty("keywordOnly").ValueKind);
        Assert.Equal(JsonValueKind.Null, index.GetProperty("embeddingModel").ValueKind);
        Assert.Equal(JsonValueKind.Null, index.GetProperty("embeddingDimensions").ValueKind);
        Assert.Equal("json", index.GetProperty("store").GetString());
    }

    [Fact]
    public async Task BuildDetailsOffKeepsPagesAndBuildDate()
    {
        using var factory = With(("Ui:ShowBuildDetails", "false"));
        using var client = factory.CreateClient();

        var html = await WaitForIndexAsync(client);
        Assert.DoesNotContain("<section id=\"build\"", html);
        Assert.DoesNotContain("href=\"#build\"", html);
        Assert.Contains("<dt>Pages</dt><dd>3</dd>", html);
        Assert.Contains("<dt>Last built</dt>", html);

        var index = (await client.GetFromJsonAsync<JsonElement>("/api/info")).GetProperty("index");
        Assert.Equal(3, index.GetProperty("pageCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, index.GetProperty("store").ValueKind);
        Assert.Equal(JsonValueKind.Null, index.GetProperty("formatVersion").ValueKind);
        Assert.Equal(JsonValueKind.Null, index.GetProperty("indexerVersion").ValueKind);
        Assert.Equal(JsonValueKind.String, index.GetProperty("builtAt").ValueKind);
    }

    [Fact]
    public void StatsKeepContentNumbersAndDropBuildNumbers()
    {
        var stats = new IndexStats
        {
            Source = "crawl", DurationSeconds = 12.5, PagesFetched = 40, PagesSkippedEmpty = 2, Errors = 1, EmbeddingsGenerated = 300, EmbeddingsFromCache = 20,
            Words = 12345, Languages = new Dictionary<string, int> { ["en"] = 40 }, Sections = [new SectionStats("/docs", "Docs", 40)],
        };

        var hiddenBuild = IndexStatsSummary.From(stats, showBuild: false, showSearch: true)!;
        Assert.Null(hiddenBuild.Source);
        Assert.Null(hiddenBuild.PagesFetched);
        Assert.Null(hiddenBuild.Errors);
        Assert.Null(hiddenBuild.EmbeddingsGenerated);
        Assert.Equal(12345, hiddenBuild.Words);
        Assert.Single(hiddenBuild.Sections);

        var hiddenSearch = IndexStatsSummary.From(stats, showBuild: true, showSearch: false)!;
        Assert.Equal(40, hiddenSearch.PagesFetched);
        Assert.Null(hiddenSearch.EmbeddingsGenerated);
        Assert.Null(hiddenSearch.EmbeddingsFromCache);

        Assert.Equal(300, IndexStatsSummary.From(stats, true, true)!.EmbeddingsGenerated);
        Assert.Null(IndexStatsSummary.From(null, true, true));
    }

    [Fact]
    public void SitemapListsOnlyThePagesThatAreServed()
    {
        var built = new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);

        Assert.Contains("<loc>https://s/</loc>", PageEndpoints.Sitemap("https://s", built, new UiOptions { ApiReference = false }));
        Assert.DoesNotContain("/docs/", PageEndpoints.Sitemap("https://s", built, new UiOptions { ApiReference = false }));
        Assert.DoesNotContain("<loc>https://s/</loc>", PageEndpoints.Sitemap("https://s", built, new UiOptions { LandingPage = false }));
    }
}
