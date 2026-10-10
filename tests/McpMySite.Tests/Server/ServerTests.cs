extern alias server;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using McpMySite.Core;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace McpMySite.Tests.Server;

/// <summary>
/// Boots the real web app against a small JSON index. No embedding key is configured, so every search
/// exercises the keyword fallback path and must report keywordOnly = true.
/// </summary>
public sealed class ServerFixture : IAsyncLifetime
{
    public WebApplicationFactory<server::Program> Factory { get; private set; } = null!;

    public HttpClient Client { get; private set; } = null!;

    public string IndexDirectory { get; private set; } = null!;

    public const string ApiKey = "test-key-123";

    public async Task InitializeAsync()
    {
        IndexDirectory = await TestIndex.WriteAsync(IndexFormat.JsonStoreType);
        Factory = new WebApplicationFactory<server::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Index:Path"] = IndexDirectory,
                ["Index:PollIntervalSeconds"] = "1",
                ["Embeddings:ApiKeyEnvironmentVariable"] = "MCPMYSITE_TEST_KEY_THAT_IS_NOT_SET",
                ["Mcp:ApiKey"] = ApiKey,
                ["SearchApi:AllowedOrigins:0"] = "https://example.test",
                ["RateLimiting:PermitPerMinute"] = "1000",
                ["Telemetry:LogQueryText"] = "true",
            }));
        });
        Client = Factory.CreateClient();

        // The index loads in a hosted service; wait until the health endpoint reports it.
        for (var i = 0; i < 100; i++)
        {
            var health = await Client.GetAsync("/healthz");
            if (health.IsSuccessStatusCode)
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("Index did not load.");
    }

    public Task DisposeAsync()
    {
        Client.Dispose();
        Factory.Dispose();
        return Task.CompletedTask;
    }
}

public class ServerTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    private HttpClient Client => fixture.Client;

    [Fact]
    public async Task HealthReportsLoadedIndex()
    {
        var health = await Client.GetFromJsonAsync<JsonElement>("/healthz");
        Assert.Equal("ok", health.GetProperty("status").GetString());
        Assert.Equal(3, health.GetProperty("pages").GetInt32());
    }

    [Fact]
    public async Task SearchApiReturnsHitsWithKeywordFallback()
    {
        var response = await Client.GetAsync("/api/search?q=kerosene&limit=5");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(body.GetProperty("keywordOnly").GetBoolean());
        var first = body.GetProperty("results")[0];
        Assert.Equal("https://example.test/docs/rockets", first.GetProperty("url").GetString());
        Assert.Contains("<mark>kerosene</mark>", first.GetProperty("highlightedSnippet").GetString());
    }

    [Fact]
    public async Task SearchApiRejectsMissingQuery()
    {
        var response = await Client.GetAsync("/api/search");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SearchApiHonoursCorsAllowlist()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/search?q=rockets");
        request.Headers.Add("Origin", "https://example.test");
        var response = await Client.SendAsync(request);
        Assert.Equal("https://example.test", response.Headers.GetValues("Access-Control-Allow-Origin").Single());

        using var denied = new HttpRequestMessage(HttpMethod.Get, "/api/search?q=rockets");
        denied.Headers.Add("Origin", "https://evil.test");
        var deniedResponse = await Client.SendAsync(denied);
        Assert.False(deniedResponse.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task McpRequiresApiKeyWhenConfigured()
    {
        var response = await Client.PostAsync("/mcp", JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "tools/list" }));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task McpListsSearchAndFetchTools()
    {
        var result = await McpCallAsync(new { jsonrpc = "2.0", id = 1, method = "tools/list" });
        var names = result.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).OrderBy(n => n).ToList();
        Assert.Equal(["fetch", "search"], names);
    }

    [Fact]
    public async Task McpSearchThenFetchRoundTrip()
    {
        var search = await McpCallAsync(new
        {
            jsonrpc = "2.0", id = 2, method = "tools/call",
            @params = new { name = "search", arguments = new { query = "kerosene", limit = 3 } },
        });
        var structured = search.GetProperty("structuredContent");
        var url = structured.GetProperty("results")[0].GetProperty("url").GetString();
        Assert.Equal("https://example.test/docs/rockets", url);

        var fetch = await McpCallAsync(new
        {
            jsonrpc = "2.0", id = 3, method = "tools/call",
            @params = new { name = "fetch", arguments = new { url } },
        });
        var page = fetch.GetProperty("structuredContent");
        Assert.Equal("Rockets", page.GetProperty("title").GetString());
        Assert.Contains("liquid oxygen", page.GetProperty("markdown").GetString());
    }

    [Fact]
    public async Task McpFetchUnknownUrlIsToolError()
    {
        var fetch = await McpCallAsync(new
        {
            jsonrpc = "2.0", id = 4, method = "tools/call",
            @params = new { name = "fetch", arguments = new { url = "https://example.test/nope" } },
        });
        Assert.True(fetch.GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task McpServerInstructionsComeFromManifest()
    {
        var init = await McpCallAsync(new
        {
            jsonrpc = "2.0", id = 5, method = "initialize",
            @params = new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "test", version = "1" } },
        });
        Assert.Equal("Test instructions.", init.GetProperty("instructions").GetString());
    }

    private async Task<JsonElement> McpCallAsync(object request)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = JsonContent.Create(request) };
        message.Headers.Accept.ParseAdd("application/json");
        message.Headers.Accept.ParseAdd("text/event-stream");
        message.Headers.Add("X-Api-Key", ServerFixture.ApiKey);

        var response = await Client.SendAsync(message);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {text}");

        // Stateless servers may answer as plain JSON or as a single SSE event.
        var json = response.Content.Headers.ContentType?.MediaType == "text/event-stream"
            ? text.Split('\n').First(l => l.StartsWith("data:"))[5..].Trim()
            : text;

        var envelope = JsonDocument.Parse(json).RootElement;
        if (envelope.TryGetProperty("error", out var error))
        {
            Assert.Fail($"JSON-RPC error: {error}");
        }

        return envelope.GetProperty("result");
    }
}
