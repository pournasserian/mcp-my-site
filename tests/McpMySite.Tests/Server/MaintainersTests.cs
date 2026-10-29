extern alias server;
using McpMySite.Server.Options;
using McpMySite.Server.Pages;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace McpMySite.Tests.Server;

// /.well-known/mcpbundles.json and /.well-known/security.txt (#83): served only once Maintainers is configured,
// independent of Ui:Enabled, so a server with the pages off can still be claimed on a directory.
public class MaintainersTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    // UseSetting, not ConfigureAppConfiguration: Program reads Maintainers before the app is built, to decide
    // whether to map the routes at all.
    private WebApplicationFactory<server::Program> With(params (string Key, string Value)[] settings) =>
        fixture.Factory.WithWebHostBuilder(builder =>
        {
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }
        });

    [Fact]
    public async Task BothRoutesAre404WhenNoMaintainersAreConfigured()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.Client.GetAsync("/.well-known/mcpbundles.json")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.Client.GetAsync("/.well-known/security.txt")).StatusCode);
    }

    [Fact]
    public async Task McpBundlesJsonListsOneMaintainerWithoutAName()
    {
        using var client = With(("Maintainers:0:Email", "owner@example.test")).CreateClient();

        var response = await client.GetAsync("/.well-known/mcpbundles.json");
        response.EnsureSuccessStatusCode();
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var maintainers = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("maintainers");
        Assert.Equal(1, maintainers.GetArrayLength());
        Assert.Equal("owner@example.test", maintainers[0].GetProperty("email").GetString());
        Assert.False(maintainers[0].TryGetProperty("name", out _));
    }

    [Fact]
    public async Task McpBundlesJsonKeepsTwoMaintainersInOrderWithOptionalNames()
    {
        using var client = With(
            ("Maintainers:0:Email", "owner@example.test"),
            ("Maintainers:1:Email", "ops@example.test"),
            ("Maintainers:1:Name", "Ops Team")).CreateClient();

        var maintainers = (await client.GetFromJsonAsync<JsonElement>("/.well-known/mcpbundles.json")).GetProperty("maintainers");

        Assert.Equal(2, maintainers.GetArrayLength());
        Assert.Equal("owner@example.test", maintainers[0].GetProperty("email").GetString());
        Assert.False(maintainers[0].TryGetProperty("name", out _));
        Assert.Equal("ops@example.test", maintainers[1].GetProperty("email").GetString());
        Assert.Equal("Ops Team", maintainers[1].GetProperty("name").GetString());
    }

    [Fact]
    public async Task SecurityTxtListsAContactPerMaintainerWithExpiresAndCanonical()
    {
        using var client = With(
            ("Maintainers:0:Email", "owner@example.test"),
            ("Maintainers:1:Email", "ops@example.test")).CreateClient();

        var response = await client.GetAsync("/.well-known/security.txt");
        response.EnsureSuccessStatusCode();
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);

        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("Contact: mailto:owner@example.test\n", text);
        Assert.Contains("Contact: mailto:ops@example.test\n", text);
        Assert.Contains("Preferred-Languages: en\n", text);
        Assert.Contains("Canonical: http://localhost/.well-known/security.txt\n", text);
        Assert.Matches(@"Expires: \d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z\n", text);
    }

    [Theory]
    [InlineData("/.well-known/mcpbundles.json")]
    [InlineData("/.well-known/security.txt")]
    public async Task BothRoutesAnswerHead(string path)
    {
        using var client = With(("Maintainers:0:Email", "owner@example.test")).CreateClient();

        using var get = await client.GetAsync(path);
        using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, path));

        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(get.StatusCode, head.StatusCode);
        Assert.Equal(get.Content.Headers.ContentType?.MediaType, head.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task RoutesStayIndependentOfUiEnabled()
    {
        using var client = With(("Maintainers:0:Email", "owner@example.test"), ("Ui:Enabled", "false")).CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/.well-known/mcpbundles.json")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/.well-known/security.txt")).StatusCode);
    }

    [Fact]
    public void SecurityTxtBuildsTheExpectedLinesForAFixedNow()
    {
        var now = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
        var maintainers = new MaintainersOptions { new() { Email = "owner@example.test" }, new() { Email = "ops@example.test", Name = "Ops" } };

        var text = PageEndpoints.SecurityTxt("https://mcp.example.test", maintainers, now);

        Assert.Equal(
            "Contact: mailto:owner@example.test\n" +
            "Contact: mailto:ops@example.test\n" +
            "Expires: 2027-10-29T12:00:00Z\n" +
            "Preferred-Languages: en\n" +
            "Canonical: https://mcp.example.test/.well-known/security.txt\n",
            text);
    }

    [Fact]
    public void McpBundlesJsonOmitsNameWhenNotSet()
    {
        var maintainers = new MaintainersOptions { new() { Email = "owner@example.test" } };

        var document = PageEndpoints.McpBundlesJson(maintainers);

        Assert.Single(document.Maintainers);
        Assert.Equal("owner@example.test", document.Maintainers[0].Email);
        Assert.Null(document.Maintainers[0].Name);
    }

    [Fact]
    public void MaintainersOptionsBindsIndexedKeysLikeMaintainers0Email()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Maintainers:0:Email"] = "owner@example.test",
                ["Maintainers:1:Email"] = "ops@example.test",
                ["Maintainers:1:Name"] = "Ops Team",
            })
            .Build();

        var maintainers = configuration.GetSection(MaintainersOptions.SectionName).Get<MaintainersOptions>();

        Assert.NotNull(maintainers);
        Assert.Equal(2, maintainers!.Count);
        Assert.Equal("owner@example.test", maintainers[0].Email);
        Assert.Null(maintainers[0].Name);
        Assert.Equal("ops@example.test", maintainers[1].Email);
        Assert.Equal("Ops Team", maintainers[1].Name);
    }
}
