extern alias server;
using McpMySite.Server.Mcp;
using System.Net;
using System.Reflection;
using System.Text.Json;

namespace McpMySite.Tests.Server;

// The server card (SEP-2127) at /mcp/server-card: public, cacheable, and describing this deployment.
public class ServerCardTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    private HttpClient Client => fixture.Client;

    private static HttpRequestMessage CardRequest()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/mcp/server-card");
        request.Headers.Accept.ParseAdd(ServerCard.MediaType);
        return request;
    }

    [Fact]
    public async Task CardDescribesThisServerWithoutAnApiKey()
    {
        using var response = await Client.SendAsync(CardRequest());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ServerCard.MediaType, response.Content.Headers.ContentType?.MediaType);

        using var card = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = card.RootElement;
        Assert.Equal(ServerCard.SchemaUrl, root.GetProperty("$schema").GetString());
        Assert.Equal(ServerCard.Name, root.GetProperty("name").GetString());
        Assert.Equal(typeof(server::Program).Assembly.GetName().Version?.ToString(3), root.GetProperty("version").GetString());
        Assert.Equal("Example (mcp-my-site)", root.GetProperty("title").GetString());
        Assert.Contains("example.test", root.GetProperty("description").GetString());
        Assert.Equal("github", root.GetProperty("repository").GetProperty("source").GetString());
        Assert.False(root.TryGetProperty("tools", out _));
        Assert.False(root.TryGetProperty("capabilities", out _));

        var remote = Assert.Single(root.GetProperty("remotes").EnumerateArray());
        Assert.Equal("streamable-http", remote.GetProperty("type").GetString());
        Assert.Equal("http://localhost/mcp", remote.GetProperty("url").GetString());
        Assert.Equal(ServerCard.SupportedProtocolVersions, remote.GetProperty("supportedProtocolVersions").EnumerateArray().Select(v => v.GetString()).ToArray());

        // The fixture requires an API key, so the card says so without revealing it.
        var header = Assert.Single(remote.GetProperty("headers").EnumerateArray());
        Assert.Equal("Authorization", header.GetProperty("name").GetString());
        Assert.True(header.GetProperty("isSecret").GetBoolean());
        Assert.DoesNotContain(ServerFixture.ApiKey, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task CardIsCacheableAndAnswers304ToItsOwnETag()
    {
        using var first = await Client.SendAsync(CardRequest());
        Assert.Equal("public, max-age=3600", first.Headers.CacheControl?.ToString());
        var etag = first.Headers.ETag;
        Assert.NotNull(etag);

        var conditional = CardRequest();
        conditional.Headers.IfNoneMatch.Add(etag);
        using var second = await Client.SendAsync(conditional);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Equal(etag, second.Headers.ETag);
        Assert.Empty(await second.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task CardAllowsAnyBrowserOrigin()
    {
        var request = CardRequest();
        request.Headers.Add("Origin", "https://directory.test");

        using var response = await Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("*", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Contains("ETag", response.Headers.GetValues("Access-Control-Expose-Headers").Single());
    }

    [Fact]
    public void SupportedProtocolVersionsMatchTheSdk()
    {
        var versions = typeof(ModelContextProtocol.McpSession).Assembly.GetType("ModelContextProtocol.Protocol.McpProtocolVersions")
            ?.GetField("SupportedProtocolVersions", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(null) as string[];

        Assert.NotNull(versions);
        Assert.Equal(versions.Order(), ServerCard.SupportedProtocolVersions.Order());
    }

    [Fact]
    public void CardWithoutAnIndexOrKeyStillDescribesTheSoftware()
    {
        var card = ServerCard.Build(null, new McpMySite.Server.Options.McpOptions(), "https://mcp.example.com");

        Assert.Equal("mcp-my-site", card.Title);
        Assert.InRange(card.Description.Length, 1, 100);
        Assert.Equal("https://mcp.example.com/mcp", card.Remotes[0].Url);
        Assert.Null(card.Remotes[0].Headers);
    }
}
