extern alias server;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using System.Net.Http.Json;
using System.Text.Json;

namespace McpMySite.Tests.Server;

/// <summary>The web app against an index built without embeddings: it must say so everywhere and never try to embed.</summary>
public sealed class KeywordOnlyServerFixture : IAsyncLifetime
{
    public WebApplicationFactory<server::Program> Factory { get; private set; } = null!;

    public HttpClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var indexDirectory = await TestIndex.WriteAsync(IndexFormat.JsonStoreType, keywordOnly: true);
        Factory = new WebApplicationFactory<server::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Index:Path"] = indexDirectory,
                ["Index:PollIntervalSeconds"] = "0",
                ["Embeddings:ApiKeyEnvironmentVariable"] = "MCPMYSITE_TEST_KEY_THAT_IS_NOT_SET",
                ["RateLimiting:PermitPerMinute"] = "1000",
            }));
        });
        Client = Factory.CreateClient();

        for (var i = 0; i < 100; i++)
        {
            if ((await Client.GetAsync("/healthz")).IsSuccessStatusCode)
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

public class KeywordOnlyServerTests(KeywordOnlyServerFixture fixture) : IClassFixture<KeywordOnlyServerFixture>
{
    private HttpClient Client => fixture.Client;

    [Fact]
    public async Task HealthAndInfoReportKeywordOnly()
    {
        var health = await Client.GetFromJsonAsync<JsonElement>("/healthz");
        Assert.Equal("ok", health.GetProperty("status").GetString());
        Assert.True(health.GetProperty("keywordOnly").GetBoolean());

        var index = (await Client.GetFromJsonAsync<JsonElement>("/api/info")).GetProperty("index");
        Assert.True(index.GetProperty("keywordOnly").GetBoolean());
        Assert.Equal(JsonValueKind.Null, index.GetProperty("embeddingModel").ValueKind);
    }

    [Fact]
    public async Task LandingPageSaysTheIndexIsKeywordOnly()
    {
        var html = await Client.GetStringAsync("/");
        Assert.Contains("This index is keyword-only", html);
        Assert.Contains("<dt>Search</dt><dd>Keyword only, no embeddings</dd>", html);
        Assert.DoesNotContain("<dt>Embedding model</dt>", html);
    }

    [Fact]
    public async Task SearchApiAnswersFromKeywordsWithoutEmbedding()
    {
        var body = await Client.GetFromJsonAsync<JsonElement>("/api/search?q=kerosene&limit=5");
        Assert.True(body.GetProperty("keywordOnly").GetBoolean());
        Assert.Equal("https://example.test/docs/rockets", body.GetProperty("results")[0].GetProperty("url").GetString());
    }

    [Fact]
    public async Task McpSearchToolDescribesKeywordSearch()
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "tools/list" }) };
        message.Headers.Accept.ParseAdd("application/json");
        message.Headers.Accept.ParseAdd("text/event-stream");
        var response = await Client.SendAsync(message);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {text}");

        var json = response.Content.Headers.ContentType?.MediaType == "text/event-stream"
            ? text.Split('\n').First(l => l.StartsWith("data:"))[5..].Trim()
            : text;
        var tools = JsonDocument.Parse(json).RootElement.GetProperty("result").GetProperty("tools").EnumerateArray();
        var search = tools.Single(t => t.GetProperty("name").GetString() == "search");
        Assert.Contains("Keyword search over its 3 pages", search.GetProperty("description").GetString());
    }
}
