extern alias server;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace McpMySite.Tests.Server;

/// <summary>The Ui:LandingPage, Ui:ApiReference and Ui:Show* switches, each on a server that differs from the shared one in that setting only.</summary>
public class UiSwitchTests(ServerFixture fixture) : IClassFixture<ServerFixture>, IDisposable
{
    private WebApplicationFactory<server::Program>? _factory;

    public void Dispose() => _factory?.Dispose();

    [Fact]
    public async Task DefaultsShowEverything()
    {
        var html = await fixture.Client.GetStringAsync("/");
        Assert.Contains("href=\"#build\"", html);
        Assert.Contains("<section id=\"build\"", html);
        Assert.Contains("<section id=\"api\"", html);
        Assert.Contains("<dt>Embedding model</dt><dd>test-model, 4 dimensions</dd>", html);
        Assert.Contains("<dt>Store</dt><dd>json, format", html);
        Assert.Contains("href=\"http://localhost/docs/\">API reference</a>", html);

        var index = (await fixture.Client.GetFromJsonAsync<JsonElement>("/api/info")).GetProperty("index");
        Assert.False(index.GetProperty("keywordOnly").GetBoolean());
        Assert.Equal("json", index.GetProperty("store").GetString());
        Assert.Equal(4, index.GetProperty("chunkCount").GetInt32());
    }

    [Fact]
    public async Task HiddenSearchApiIsStillServed()
    {
        using var client = await ClientWithAsync(("Ui:ShowSearchApi", "false"));

        var html = await client.GetStringAsync("/");
        Assert.DoesNotContain("<section id=\"api\"", html);
        Assert.DoesNotContain("/api/search", html);
        Assert.Contains("http://localhost/mcp", html);

        var endpoints = (await client.GetFromJsonAsync<JsonElement>("/api/info")).GetProperty("endpoints");
        Assert.Equal(JsonValueKind.Null, endpoints.GetProperty("searchApi").ValueKind);
        Assert.Equal("http://localhost/docs/", endpoints.GetProperty("docs").GetString());

        var llms = await client.GetStringAsync("/llms.txt");
        Assert.DoesNotContain("/api/search", llms);
        Assert.Contains("(tools `search` and `fetch`).\n", llms);

        // Hiding is not access control.
        var search = await client.GetAsync("/api/search?q=rockets");
        Assert.Equal(HttpStatusCode.OK, search.StatusCode);
    }

    [Fact]
    public async Task HiddenSearchDetailsLeaveTheModeAndModelOut()
    {
        using var client = await ClientWithAsync(("Ui:ShowSearchDetails", "false"));

        var html = await client.GetStringAsync("/");
        Assert.DoesNotContain("Embedding model", html);
        Assert.DoesNotContain("test-model", html);
        Assert.DoesNotContain("Keyword only", html);
        Assert.DoesNotContain("keyword-only", html);
        Assert.Contains("<section id=\"build\"", html);
        Assert.Contains("<dt>Store</dt><dd>json, format", html);

        var index = (await client.GetFromJsonAsync<JsonElement>("/api/info")).GetProperty("index");
        Assert.Equal(JsonValueKind.Null, index.GetProperty("keywordOnly").ValueKind);
        Assert.Equal(JsonValueKind.Null, index.GetProperty("embeddingModel").ValueKind);
        Assert.Equal(JsonValueKind.Null, index.GetProperty("embeddingDimensions").ValueKind);
        Assert.Equal("json", index.GetProperty("store").GetString());

        // The search answer describes itself as before.
        var search = await client.GetFromJsonAsync<JsonElement>("/api/search?q=rockets");
        Assert.True(search.GetProperty("keywordOnly").GetBoolean());
    }

    [Fact]
    public async Task HiddenBuildDetailsKeepPagesAndBuildDate()
    {
        using var client = await ClientWithAsync(("Ui:ShowBuildDetails", "false"));

        var html = await client.GetStringAsync("/");
        Assert.DoesNotContain("href=\"#build\"", html);
        Assert.DoesNotContain("<section id=\"build\"", html);
        Assert.DoesNotContain("<dt>Store</dt>", html);
        Assert.DoesNotContain("<dt>Chunks</dt>", html);
        Assert.DoesNotContain("test-model", html);
        Assert.Contains("<dt>Pages</dt><dd>3</dd>", html);
        Assert.Contains("<dt>Last built</dt><dd>2026-10-10", html);
        Assert.Contains("<section id=\"api\"", html);

        var index = (await client.GetFromJsonAsync<JsonElement>("/api/info")).GetProperty("index");
        Assert.Equal(3, index.GetProperty("pageCount").GetInt32());
        Assert.StartsWith("2026-10-10", index.GetProperty("builtAt").GetString());
        foreach (var name in new[] { "loadedAt", "chunkCount", "store", "formatVersion", "indexerVersion", "stats" })
        {
            Assert.Equal(JsonValueKind.Null, index.GetProperty(name).ValueKind);
        }

        // Search details are a separate switch.
        Assert.False(index.GetProperty("keywordOnly").GetBoolean());
    }

    [Fact]
    public async Task ApiReferenceOffKeepsTheOpenApiDocument()
    {
        using var client = await ClientWithAsync(("Ui:ApiReference", "false"));

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/docs/")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/docs")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/openapi/v1.json")).StatusCode);

        var html = await client.GetStringAsync("/");
        Assert.DoesNotContain("http://localhost/docs/", html);
        Assert.DoesNotContain("API reference", html);
        Assert.Contains("\"documentation\":\"http://localhost/openapi/v1.json\"", html);
        Assert.Contains("<section id=\"api\"", html);

        var sitemap = await client.GetStringAsync("/sitemap.xml");
        Assert.Contains("<loc>http://localhost/</loc>", sitemap);
        Assert.DoesNotContain("/docs/", sitemap);

        var endpoints = (await client.GetFromJsonAsync<JsonElement>("/api/info")).GetProperty("endpoints");
        Assert.Equal(JsonValueKind.Null, endpoints.GetProperty("docs").ValueKind);
        Assert.Equal("http://localhost/openapi/v1.json", endpoints.GetProperty("openApi").GetString());
    }

    [Fact]
    public async Task LandingPageOffServesTheTextSummaryAndKeepsTheOtherPages()
    {
        using var client = await ClientWithAsync(allowAutoRedirect: false, ("Ui:LandingPage", "false"));

        var home = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        Assert.Equal("text/plain", home.Content.Headers.ContentType?.MediaType);
        var text = await home.Content.ReadAsStringAsync();
        Assert.StartsWith("mcp-my-site for Example\nMCP endpoint: /mcp\n", text);
        Assert.Contains("Search API: /api/search?q=...", text);

        // No landing page to send a browser to: the request reaches the MCP middleware, here the key check.
        var request = new HttpRequestMessage(HttpMethod.Get, "/mcp");
        request.Headers.Accept.ParseAdd("text/html");
        using var browser = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, browser.StatusCode);
        Assert.Null(browser.Headers.Location);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/playground")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/docs/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/llms.txt")).StatusCode);
        Assert.Contains("Sitemap: http://localhost/sitemap.xml", await client.GetStringAsync("/robots.txt"));

        var sitemap = await client.GetStringAsync("/sitemap.xml");
        Assert.DoesNotContain("<loc>http://localhost/</loc>", sitemap);
        Assert.Contains("<loc>http://localhost/docs/</loc>", sitemap);
    }

    [Fact]
    public async Task TextSummaryLeavesOutAHiddenSearchApi()
    {
        using var client = await ClientWithAsync(("Ui:LandingPage", "false"), ("Ui:ShowSearchApi", "false"));

        var text = await client.GetStringAsync("/");
        Assert.Equal("mcp-my-site for Example\nMCP endpoint: /mcp\n", text);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/search?q=rockets")).StatusCode);
    }

    private Task<HttpClient> ClientWithAsync(params (string Key, string Value)[] settings) => ClientWithAsync(true, settings);

    /// <summary>A server like the shared one with the given settings, once its index has loaded. UseSetting, since Program reads the Ui options before the app is built.</summary>
    private async Task<HttpClient> ClientWithAsync(bool allowAutoRedirect, params (string Key, string Value)[] settings)
    {
        _factory = fixture.Factory.WithWebHostBuilder(builder =>
        {
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }
        });
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = allowAutoRedirect });

        for (var i = 0; i < 100; i++)
        {
            if ((await client.GetAsync("/healthz")).IsSuccessStatusCode)
            {
                return client;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("Index did not load.");
    }
}
