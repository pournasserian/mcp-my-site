extern alias server;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using System.Net;

namespace McpMySite.Tests.Server;

// HEAD on every route, and what a browser gets when it opens the MCP endpoint's URL.
public class MethodTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    private HttpClient Client => fixture.Client;

    [Theory]
    [InlineData("/")]
    [InlineData("/llms.txt")]
    [InlineData("/robots.txt")]
    [InlineData("/sitemap.xml")]
    [InlineData("/api/info")]
    [InlineData("/mcp/server-card")]
    [InlineData("/api/search?q=rockets")]
    [InlineData("/healthz")]
    [InlineData("/openapi/v1.json")]
    [InlineData("/docs/")]
    public async Task HeadAnswersLikeGet(string path)
    {
        using var get = await Client.GetAsync(path);
        using var head = await Client.SendAsync(new HttpRequestMessage(HttpMethod.Head, path));

        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(get.StatusCode, head.StatusCode);
        Assert.Equal(get.Content.Headers.ContentType?.MediaType, head.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task OpenApiDocumentDescribesGetOnly()
    {
        var document = await Client.GetStringAsync("/openapi/v1.json");

        Assert.Contains("\"get\":", document);
        Assert.DoesNotContain("\"head\":", document);
    }

    [Fact]
    public async Task BrowserOnTheMcpEndpointIsSentToTheLandingPage()
    {
        using var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var request = new HttpRequestMessage(HttpMethod.Get, "/mcp");
        request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.ToString());
    }

    [Theory]
    [InlineData("HEAD", "/mcp")]
    [InlineData("GET", "/mcp/")]
    public async Task HeadAndTrailingSlashOnTheMcpEndpointAreSentToTheLandingPageToo(string method, string path)
    {
        using var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task ClientAskingForAnEventStreamReachesTheTransport()
    {
        using var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var request = new HttpRequestMessage(HttpMethod.Get, "/mcp");
        request.Headers.Accept.ParseAdd("text/event-stream");

        using var response = await client.SendAsync(request);

        // The fixture requires an API key, so the key check answers first: the point is that nothing redirected.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task WithoutPagesTheTransportAnswersBrowsersItself()
    {
        using var factory = new WebApplicationFactory<server::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Index:Path"] = fixture.IndexDirectory,
                ["Embeddings:ApiKeyEnvironmentVariable"] = "MCPMYSITE_TEST_KEY_THAT_IS_NOT_SET",
                ["Ui:Enabled"] = "false",
            }));
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var request = new HttpRequestMessage(HttpMethod.Get, "/mcp");
        request.Headers.Accept.ParseAdd("text/html");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);

        using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/"));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
    }
}
