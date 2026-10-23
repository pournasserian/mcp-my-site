using McpMySite.Server.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace McpMySite.Tests.Server;

/// <summary>The landing page, /api/info and the files for crawlers, against the shared test server.</summary>
public class PageTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    private HttpClient Client => fixture.Client;

    [Fact]
    public async Task LandingPageIsServerRenderedWithSearchMetadata()
    {
        var response = await Client.GetAsync("/");
        response.EnsureSuccessStatusCode();
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("<title>Example MCP server and search API</title>", html);
        Assert.Contains("<link rel=\"canonical\" href=\"http://localhost/\"", html);
        Assert.Contains("<meta name=\"robots\" content=\"index, follow\"", html);
        Assert.Contains("<script type=\"application/ld+json\">", html);
        Assert.Contains("\"@type\":\"WebAPI\"", html);

        // Everything a reader needs is in the HTML itself, not loaded by script.
        Assert.Contains("http://localhost/mcp", html);
        Assert.Contains("claude mcp add --transport http example-test http://localhost/mcp --header", html);
        Assert.Contains("<dt>Pages</dt><dd>3</dd>", html);
        Assert.Contains("http://localhost/api/search?q=", html);
    }

    [Fact]
    public async Task LandingPageWarnsThatTheServerNeedsAKey()
    {
        var html = await Client.GetStringAsync("/");
        Assert.Contains("This server needs an API key", html);

        // Claude.ai and ChatGPT cannot send the key, so their instructions are left out.
        Assert.DoesNotContain("<summary>ChatGPT</summary>", html);
    }

    [Fact]
    public async Task LandingPageExplainsHowToTestTheServerWithTheInspector()
    {
        var html = await Client.GetStringAsync("/");

        Assert.Contains("href=\"#test\"", html);
        Assert.Contains("<section id=\"test\"", html);
        Assert.Contains("npx @modelcontextprotocol/inspector</code>", html);
        Assert.Contains("npx @modelcontextprotocol/inspector --cli http://localhost/mcp --transport http --header &quot;Authorization: Bearer &lt;key&gt;&quot; --method tools/call --tool-name search --tool-arg query=pricing", html);
        Assert.Contains("under Authentication, add an <code>Authorization</code> header", html);
        Assert.Contains("https://github.com/modelcontextprotocol/inspector#readme", html);
    }

    [Fact]
    public async Task OldPlaygroundLinksAreRedirectedToTheTestSection()
    {
        using var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await client.GetAsync("/playground");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/#test", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task InfoApiDescribesSiteIndexAndEndpoints()
    {
        var info = await Client.GetFromJsonAsync<JsonElement>("/api/info");

        Assert.Equal("ok", info.GetProperty("status").GetString());
        Assert.Equal("Example", info.GetProperty("site").GetProperty("name").GetString());
        Assert.Equal(3, info.GetProperty("index").GetProperty("pageCount").GetInt32());
        Assert.False(info.GetProperty("index").TryGetProperty("stats", out var stats) && stats.ValueKind != JsonValueKind.Null);

        var endpoints = info.GetProperty("endpoints");
        Assert.Equal("http://localhost/mcp", endpoints.GetProperty("mcp").GetString());
        Assert.True(endpoints.GetProperty("mcpRequiresKey").GetBoolean());
        Assert.Equal("http://localhost/docs/", endpoints.GetProperty("docs").GetString());
    }

    [Fact]
    public async Task RobotsAndSitemapPointCrawlersAtThePages()
    {
        var robots = await Client.GetStringAsync("/robots.txt");
        Assert.Contains("Disallow: /mcp\n", robots);
        Assert.Contains("Disallow: /api/\n", robots);
        Assert.Contains("Sitemap: http://localhost/sitemap.xml", robots);

        var sitemap = await Client.GetAsync("/sitemap.xml");
        Assert.Equal("application/xml", sitemap.Content.Headers.ContentType?.MediaType);
        var xml = await sitemap.Content.ReadAsStringAsync();
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", xml);
        Assert.Contains("<loc>http://localhost/</loc>", xml);
        Assert.Contains("<loc>http://localhost/docs/</loc>", xml);
        Assert.DoesNotContain("/playground", xml);
        Assert.Contains("<lastmod>2026-10-10</lastmod>", xml);
    }

    [Fact]
    public async Task LlmsTxtListsEveryPageBySection()
    {
        var response = await Client.GetAsync("/llms.txt");
        response.EnsureSuccessStatusCode();
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        var text = await response.Content.ReadAsStringAsync();

        Assert.StartsWith("# Example\n\nThe 3 pages of https://example.test/, as indexed on 2026-10-10.", text);
        Assert.Contains("http://localhost/mcp", text);
        Assert.Contains("## /docs\n\n- [Rockets](https://example.test/docs/rockets)\n- [Widgets](https://example.test/docs/widgets)\n", text);
        Assert.Contains("## Home\n\n- [Home](https://example.test/): Welcome page\n", text);
        Assert.Contains("href=\"/llms.txt\"", await Client.GetStringAsync("/"));
    }

    [Fact]
    public void LlmsTxtUsesSectionNamesAndEscapesLinkText()
    {
        var manifest = TestIndex.Manifest(IndexFormat.JsonStoreType) with
        {
            SiteDescription = "Rockets\nand widgets.",
            Stats = new IndexStats { Sections = [new SectionStats("/docs", "Documentation", 2), new SectionStats("/", null, 1)] },
        };
        var index = new IndexSummary(manifest.BuiltAt, null, false, 3, 4, "json", 1, false, "m", 4, null, IndexStatsSummary.From(manifest.Stats, true, true));
        var info = new SiteInfo("ok", new SiteSummary("Example", manifest.SiteUrl, manifest.SiteDescription, "en"), index,
            new EndpointUrls("https://mcp.example.test/mcp", false, null, "https://mcp.example.test/openapi/v1.json", null), new ServerSummary("s", "1"));
        PageDocument[] pages = [.. TestIndex.Pages, new() { Id = 4, Url = "https://example.test/docs/brackets", Title = "Use [square] brackets", Markdown = "x" }];

        var text = McpMySite.Server.Pages.LlmsTxt.Build(info, pages);

        Assert.StartsWith("# Example\n\n> Rockets and widgets.\n\n", text);
        Assert.Contains("(tools `search` and `fetch`).\n", text);
        Assert.Contains("## Documentation\n", text);
        Assert.True(text.IndexOf("## Documentation", StringComparison.Ordinal) < text.IndexOf("## Home", StringComparison.Ordinal));
        Assert.Contains("- [Use \\[square\\] brackets](https://example.test/docs/brackets)\n", text);
    }

    [Fact]
    public async Task PublicUrlAndStaleIndexAreReflected()
    {
        using var factory = fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["Ui:PublicUrl"] = "https://mcp.example.test/", ["Ui:StaleAfterDays"] = "1" })));
        using var client = factory.CreateClient();

        var html = await WaitForIndexAsync(client);
        Assert.Contains("<link rel=\"canonical\" href=\"https://mcp.example.test/\"", html);
        Assert.Contains("https://mcp.example.test/mcp", html);
        Assert.Contains("This index was built on 2026-10-10", html);
    }

    [Fact]
    public async Task WithoutAnIndexTheLandingPageIsUnavailableAndNotIndexed()
    {
        using var factory = fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["Index:Path"] = Path.Combine(Path.GetTempPath(), "mcpmysite-tests", "missing") })));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("No index loaded yet", html);
        Assert.Contains("<meta name=\"robots\" content=\"noindex\"", html);

        var info = await client.GetAsync("/api/info");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, info.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/llms.txt")).StatusCode);
    }

    [Fact]
    public async Task PagesCanBeTurnedOff()
    {
        // UseSetting, not ConfigureAppConfiguration: Program reads which endpoints to map before the app is built.
        using var factory = fixture.Factory.WithWebHostBuilder(builder => builder.UseSetting("Ui:Enabled", "false"));
        using var client = factory.CreateClient();

        var home = await client.GetAsync("/");
        Assert.Equal("text/plain", home.Content.Headers.ContentType?.MediaType);
        Assert.Equal("User-agent: *\nDisallow: /\n", await client.GetStringAsync("/robots.txt"));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/sitemap.xml")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/playground")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/llms.txt")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/docs/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/openapi/v1.json")).StatusCode);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/docs/")]
    [InlineData("/api/info")]
    [InlineData("/api/search?q=rockets")]
    [InlineData("/healthz")]
    [InlineData("/no-such-page")]
    public async Task EveryResponseDeniesFramingAndSniffing(string path)
    {
        var response = await Client.GetAsync(path);

        Assert.Equal("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("strict-origin-when-cross-origin", response.Headers.GetValues("Referrer-Policy").Single());
    }

    [Fact]
    public async Task FrameAncestorsCanBeAllowed()
    {
        using var factory = fixture.Factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Ui:FrameAncestors:0", "https://example.test");
            builder.UseSetting("Ui:FrameAncestors:1", "'self'");
        });
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/");
        Assert.Equal("frame-ancestors https://example.test 'self'", response.Headers.GetValues("Content-Security-Policy").Single());

        // X-Frame-Options cannot carry a list, so only frame-ancestors speaks.
        Assert.False(response.Headers.Contains("X-Frame-Options"));
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }

    /// <summary>A new test server loads its index in the background; poll the page until it shows one.</summary>
    private static async Task<string> WaitForIndexAsync(HttpClient client)
    {
        for (var i = 0; i < 100; i++)
        {
            var response = await client.GetAsync("/");
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadAsStringAsync();
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("Index did not load.");
    }
}
