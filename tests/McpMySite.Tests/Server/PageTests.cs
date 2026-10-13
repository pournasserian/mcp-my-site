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
        Assert.Contains("<lastmod>2026-10-10</lastmod>", xml);
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
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/docs/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/openapi/v1.json")).StatusCode);
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
