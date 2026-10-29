extern alias server;
using McpMySite.Server.Options;
using McpMySite.Server.Requests;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;

namespace McpMySite.Tests.Server;

/// <summary>Stands in for the proxy in front of the server: the test says which address the connection came from.</summary>
internal sealed class TestPeerStartupFilter : IStartupFilter
{
    public const string Header = "X-Test-Peer";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, nextMiddleware) =>
        {
            if (context.Request.Headers.TryGetValue(Header, out var peer))
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(peer.ToString());
            }

            return nextMiddleware(context);
        });
        next(app);
    };
}

public class CloudflareClientIpMiddlewareTests
{
    private static readonly string[] Ranges = ["173.245.48.0/20", "2400:cb00::/32"];

    private static async Task<(HttpContext Context, Activity Activity)> RunAsync(string? peer, Action<IHeaderDictionary> headers, string[]? ranges = null)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = peer is null ? null : IPAddress.Parse(peer);
        headers(context.Request.Headers);

        var activity = new Activity("test-request").Start();
        var middleware = new CloudflareClientIpMiddleware(_ => Task.CompletedTask, Options.Create(new CloudflareOptions { TrustedNetworks = ranges ?? Ranges }));
        await middleware.InvokeAsync(context);
        activity.Stop();
        return (context, activity);
    }

    [Fact]
    public async Task ConnectionFromCloudflareTakesTheVisitorAddress()
    {
        var (context, activity) = await RunAsync("173.245.48.7", h =>
        {
            h["CF-Connecting-IP"] = "203.0.113.9";
            h["CF-Ray"] = "8a1b2c3d4e5f6a7b-AMS";
            h["CF-IPCountry"] = "NL";
        });

        Assert.Equal(IPAddress.Parse("203.0.113.9"), context.Connection.RemoteIpAddress);
        Assert.Equal("203.0.113.9", activity.GetTagItem("client.address"));
        Assert.Equal("8a1b2c3d4e5f6a7b-AMS", activity.GetTagItem("cloudflare.ray_id"));
        Assert.Equal("NL", activity.GetTagItem("cloudflare.country"));
    }

    [Theory]
    [InlineData("198.51.100.7")]
    [InlineData("2001:db8::1")]
    [InlineData("173.245.64.1")]
    public async Task ConnectionFromAnywhereElseKeepsItsOwnAddress(string peer)
    {
        var (context, activity) = await RunAsync(peer, h =>
        {
            h["CF-Connecting-IP"] = "203.0.113.9";
            h["CF-Ray"] = "8a1b2c3d4e5f6a7b-AMS";
            h["CF-IPCountry"] = "NL";
        });

        Assert.Equal(IPAddress.Parse(peer), context.Connection.RemoteIpAddress);
        Assert.Null(activity.GetTagItem("client.address"));
        Assert.Null(activity.GetTagItem("cloudflare.ray_id"));
        Assert.Null(activity.GetTagItem("cloudflare.country"));
    }

    [Theory]
    [InlineData("2400:cb00::5")]
    [InlineData("::ffff:173.245.48.5")]
    public async Task Ipv6AndMappedIpv4PeersAreMatchedAgainstTheRanges(string peer)
    {
        var (context, _) = await RunAsync(peer, h => h["CF-Connecting-IP"] = "2001:db8::42");

        Assert.Equal(IPAddress.Parse("2001:db8::42"), context.Connection.RemoteIpAddress);
    }

    [Fact]
    public async Task ConnectionWithoutAnAddressIsLeftAlone()
    {
        var (context, activity) = await RunAsync(null, h => h["CF-Connecting-IP"] = "203.0.113.9");

        Assert.Null(context.Connection.RemoteIpAddress);
        Assert.Null(activity.GetTagItem("client.address"));
    }

    [Theory]
    [InlineData("not-an-ip")]
    [InlineData("1")]
    [InlineData("203.0.113")]
    [InlineData("203.0.113.9, 198.51.100.1")]
    [InlineData("")]
    public async Task MalformedVisitorAddressIsIgnored(string value)
    {
        var (context, activity) = await RunAsync("173.245.48.7", h => h["CF-Connecting-IP"] = value);

        Assert.Equal(IPAddress.Parse("173.245.48.7"), context.Connection.RemoteIpAddress);
        Assert.Null(activity.GetTagItem("client.address"));
    }

    [Fact]
    public async Task RepeatedHeaderIsNotBelieved()
    {
        var (context, _) = await RunAsync("173.245.48.7", h => h["CF-Connecting-IP"] = new Microsoft.Extensions.Primitives.StringValues(["203.0.113.9", "203.0.113.10"]));

        Assert.Equal(IPAddress.Parse("173.245.48.7"), context.Connection.RemoteIpAddress);
    }

    [Fact]
    public async Task RayAndCountryThatDoNotLookRightAreNotTagged()
    {
        var (_, activity) = await RunAsync("173.245.48.7", h =>
        {
            h["CF-Ray"] = "<script>alert(1)</script>";
            h["CF-IPCountry"] = "Netherlands";
        });

        Assert.Null(activity.GetTagItem("cloudflare.ray_id"));
        Assert.Null(activity.GetTagItem("cloudflare.country"));
    }

    [Fact]
    public void RangeThatIsNeitherCidrNorAFullAddressFailsAtStartup()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            new CloudflareClientIpMiddleware(_ => Task.CompletedTask, Options.Create(new CloudflareOptions { TrustedNetworks = ["173.245.48"] })));

        Assert.Contains("173.245.48", error.Message);
    }
}

public class CloudflareServerTests
{
    private const string CloudflareEdge = "173.245.48.7";

    private static async Task<(WebApplicationFactory<server::Program> Factory, HttpClient Client)> StartAsync(Dictionary<string, string?> settings) =>
        await LoggedServer.StartAsync(new CapturedLogs(), settings, configureServices: services => services.AddTransient<IStartupFilter, TestPeerStartupFilter>());

    private static HttpRequestMessage Search(string peer, string? visitor, string? ray = null, string? country = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/search?q=rockets");
        request.Headers.Add(TestPeerStartupFilter.Header, peer);
        if (visitor is not null)
        {
            request.Headers.Add("CF-Connecting-IP", visitor);
        }

        if (ray is not null)
        {
            request.Headers.Add("CF-Ray", ray);
        }

        if (country is not null)
        {
            request.Headers.Add("CF-IPCountry", country);
        }

        return request;
    }

    [Fact]
    public async Task RateLimitCountsEachVisitorBehindCloudflareSeparately()
    {
        var (factory, client) = await StartAsync(new() { ["RateLimiting:PermitPerMinute"] = "1" });
        using (factory)
        using (client)
        {
            // Everyone arrives from the same Cloudflare address; each visitor still gets their own window.
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Search(CloudflareEdge, "203.0.113.1"))).StatusCode);
            Assert.Equal(HttpStatusCode.TooManyRequests, (await client.SendAsync(Search(CloudflareEdge, "203.0.113.1"))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Search(CloudflareEdge, "203.0.113.2"))).StatusCode);
        }
    }

    [Fact]
    public async Task ForgedHeaderFromOutsideCloudflareDoesNotEscapeTheRateLimit()
    {
        var (factory, client) = await StartAsync(new() { ["RateLimiting:PermitPerMinute"] = "1" });
        using (factory)
        using (client)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Search("198.51.100.7", "203.0.113.1"))).StatusCode);
            Assert.Equal(HttpStatusCode.TooManyRequests, (await client.SendAsync(Search("198.51.100.7", "203.0.113.2"))).StatusCode);
        }
    }

    [Fact]
    public async Task SettingOffIgnoresTheHeaderEvenFromCloudflare()
    {
        var (factory, client) = await StartAsync(new() { ["RateLimiting:PermitPerMinute"] = "1", ["Cloudflare:TrustConnectingIp"] = "false" });
        using (factory)
        using (client)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Search(CloudflareEdge, "203.0.113.1"))).StatusCode);
            Assert.Equal(HttpStatusCode.TooManyRequests, (await client.SendAsync(Search(CloudflareEdge, "203.0.113.2"))).StatusCode);
        }
    }

    [Fact]
    public async Task RequestSpanCarriesRayCountryAndClientAddress()
    {
        var spans = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Microsoft.AspNetCore",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = spans.Add,
        };
        ActivitySource.AddActivityListener(listener);

        var (factory, client) = await StartAsync([]);
        using (factory)
        using (client)
        {
            (await client.SendAsync(Search(CloudflareEdge, "203.0.113.9", "8a1b2c3d4e5f6a7b-AMS", "NL"))).EnsureSuccessStatusCode();
            (await client.SendAsync(Search("198.51.100.7", "203.0.113.10", "ffffffffffffffff-SIN", "SG"))).EnsureSuccessStatusCode();
        }

        var viaCloudflare = Assert.Single(spans, s => s.GetTagItem("cloudflare.ray_id") as string == "8a1b2c3d4e5f6a7b-AMS");
        Assert.Equal("NL", viaCloudflare.GetTagItem("cloudflare.country"));
        Assert.Equal("203.0.113.9", viaCloudflare.GetTagItem("client.address"));

        // The second request did not come from Cloudflare, so its headers are not believed and not recorded.
        Assert.DoesNotContain(spans, s => s.GetTagItem("cloudflare.ray_id") as string == "ffffffffffffffff-SIN");
    }
}

public class ClientIpResolverTests
{
    private static HttpContext Context(string? peer, string? header = null, string? headerValue = null)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = peer is null ? null : IPAddress.Parse(peer);
        if (header is not null && headerValue is not null)
        {
            context.Request.Headers[header] = headerValue;
        }

        return context;
    }

    private static ClientIpResolver Resolver(string[] trustedProxies, string? clientIpHeader = "X-Forwarded-For") =>
        new(Options.Create(new SearchApiOptions { TrustedProxies = trustedProxies, ClientIpHeader = clientIpHeader }));

    [Fact]
    public void RequestFromATrustedProxyIsPartitionedByTheForwardedAddress()
    {
        var resolver = Resolver(["10.0.0.0/8"]);
        var context = Context("10.1.2.3", "X-Forwarded-For", "203.0.113.9");

        Assert.Equal("203.0.113.9", resolver.Resolve(context));
    }

    [Fact]
    public void AForwardedForChainKeepsOnlyTheFirstAddress()
    {
        var resolver = Resolver(["10.0.0.0/8"]);
        var context = Context("10.1.2.3", "X-Forwarded-For", "203.0.113.9, 198.51.100.1");

        Assert.Equal("203.0.113.9", resolver.Resolve(context));
    }

    [Fact]
    public void RequestNotFromATrustedProxyKeepsItsOwnAddress()
    {
        var resolver = Resolver(["10.0.0.0/8"]);
        var context = Context("198.51.100.7", "X-Forwarded-For", "203.0.113.9");

        Assert.Equal("198.51.100.7", resolver.Resolve(context));
    }

    [Fact]
    public void NoTrustedProxiesConfiguredKeepsTheRawAddressEvenWithTheHeaderPresent()
    {
        var resolver = Resolver([]);
        var context = Context("10.1.2.3", "X-Forwarded-For", "203.0.113.9");

        Assert.Equal("10.1.2.3", resolver.Resolve(context));
    }

    [Fact]
    public void NoClientIpHeaderConfiguredKeepsTheRawAddress()
    {
        var resolver = Resolver(["10.0.0.0/8"], clientIpHeader: null);
        var context = Context("10.1.2.3", "X-Forwarded-For", "203.0.113.9");

        Assert.Equal("10.1.2.3", resolver.Resolve(context));
    }

    [Theory]
    [InlineData("not-an-ip")]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("203.0.113")]
    public void MalformedForwardedHeaderFallsBackToTheRawAddress(string value)
    {
        var resolver = Resolver(["10.0.0.0/8"]);
        var context = Context("10.1.2.3", "X-Forwarded-For", value);

        Assert.Equal("10.1.2.3", resolver.Resolve(context));
    }

    [Fact]
    public void RepeatedHeaderIsNotBelieved()
    {
        var resolver = Resolver(["10.0.0.0/8"]);
        var context = Context("10.1.2.3");
        context.Request.Headers["X-Forwarded-For"] = new Microsoft.Extensions.Primitives.StringValues(["203.0.113.9", "203.0.113.10"]);

        Assert.Equal("10.1.2.3", resolver.Resolve(context));
    }

    [Fact]
    public void ConnectionWithoutAnAddressIsReportedAsUnknown()
    {
        var resolver = Resolver(["10.0.0.0/8"]);
        var context = Context(null, "X-Forwarded-For", "203.0.113.9");

        Assert.Equal("unknown", resolver.Resolve(context));
    }

    [Fact]
    public void ASingleAddressTrustsExactlyThatPeer()
    {
        var resolver = Resolver(["10.1.2.3"]);

        Assert.Equal("203.0.113.9", resolver.Resolve(Context("10.1.2.3", "X-Forwarded-For", "203.0.113.9")));
        Assert.Equal("10.1.2.4", resolver.Resolve(Context("10.1.2.4", "X-Forwarded-For", "203.0.113.9")));
    }

    [Fact]
    public void EntryThatIsNeitherAnAddressNorACidrFailsAtStartup()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Resolver(["not-an-address"]));

        Assert.Contains("not-an-address", error.Message);
    }
}

public class SearchApiTrustedProxyServerTests
{
    private const string Proxy = "10.0.0.1";

    private static async Task<(WebApplicationFactory<server::Program> Factory, HttpClient Client)> StartAsync(Dictionary<string, string?> settings) =>
        await LoggedServer.StartAsync(new CapturedLogs(), settings, configureServices: services => services.AddTransient<IStartupFilter, TestPeerStartupFilter>());

    // A header distinct from X-Forwarded-For: RateLimiting:UseForwardedHeaders (on by default) already consumes
    // that one for the platform's own reverse proxy, before this policy's partition key is ever computed.
    private const string ClientIpHeader = "CF-Connecting-IP";

    private static HttpRequestMessage Search(string peer, string? clientIp = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/search?q=rockets");
        request.Headers.Add(TestPeerStartupFilter.Header, peer);
        if (clientIp is not null)
        {
            request.Headers.Add(ClientIpHeader, clientIp);
        }

        return request;
    }

    [Fact]
    public async Task RateLimitCountsEachVisitorBehindTheTrustedProxySeparately()
    {
        var (factory, client) = await StartAsync(new()
        {
            ["RateLimiting:PermitPerMinute"] = "1",
            ["SearchApi:TrustedProxies:0"] = "10.0.0.0/8",
            ["SearchApi:ClientIpHeader"] = ClientIpHeader,
        });
        using (factory)
        using (client)
        {
            // Everyone arrives from the same proxy address; each visitor still gets their own window.
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Search(Proxy, "203.0.113.1"))).StatusCode);
            Assert.Equal(HttpStatusCode.TooManyRequests, (await client.SendAsync(Search(Proxy, "203.0.113.1"))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Search(Proxy, "203.0.113.2"))).StatusCode);
        }
    }

    [Fact]
    public async Task ForgedHeaderFromOutsideTheTrustedProxyDoesNotEscapeTheRateLimit()
    {
        var (factory, client) = await StartAsync(new()
        {
            ["RateLimiting:PermitPerMinute"] = "1",
            ["SearchApi:TrustedProxies:0"] = "10.0.0.0/8",
            ["SearchApi:ClientIpHeader"] = ClientIpHeader,
        });
        using (factory)
        using (client)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Search("198.51.100.7", "203.0.113.1"))).StatusCode);
            Assert.Equal(HttpStatusCode.TooManyRequests, (await client.SendAsync(Search("198.51.100.7", "203.0.113.2"))).StatusCode);
        }
    }

    [Fact]
    public async Task EntryThatIsNotAnAddressStopsTheServerStarting()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() => StartAsync(new() { ["SearchApi:TrustedProxies:0"] = "not-an-address" }));

        Assert.Contains("SearchApi:TrustedProxies entry 'not-an-address'", error.ToString());
    }

    [Fact]
    public async Task UnconfiguredByDefaultPartitionsByTheRawAddressEvenWithTheHeaderPresent()
    {
        var (factory, client) = await StartAsync(new() { ["RateLimiting:PermitPerMinute"] = "1" });
        using (factory)
        using (client)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Search(Proxy, "203.0.113.1"))).StatusCode);
            Assert.Equal(HttpStatusCode.TooManyRequests, (await client.SendAsync(Search(Proxy, "203.0.113.2"))).StatusCode);
        }
    }
}

public class SecretRedactorTests
{
    private static readonly SecretRedactor Redactor = new(["server-side-key-value"]);

    [Theory]
    [InlineData("""{"api_key":"abc123","query":"rockets"}""", """{"api_key":"[redacted]","query":"rockets"}""")]
    [InlineData("""{"Authorization": "Bearer abc.def"}""", """{"Authorization": "[redacted]"}""")]
    [InlineData("""{"client_secret":"s3cr3t","limit":3}""", """{"client_secret":"[redacted]","limit":3}""")]
    [InlineData("{\"query\":\"rockets\",\"token\":\"abcdef", "{\"query\":\"rockets\",\"token\":\"[redacted]\"")]
    [InlineData("?q=rockets&api_key=abc&limit=3", "?q=rockets&api_key=[redacted]&limit=3")]
    [InlineData("?sv=2026&sig=TOPSECRET%3D", "?sv=2026&sig=[redacted]")]
    [InlineData("Authorization: Bearer abc.def-123", "Authorization: Bearer [redacted]")]
    [InlineData("key is sk-or-v1-0123456789abcdef0123456789", "key is [redacted]")]
    [InlineData("the value server-side-key-value appears", "the value [redacted] appears")]
    [InlineData("""{"query":"what is a token ring","limit":3}""", """{"query":"what is a token ring","limit":3}""")]
    [InlineData("?q=monkey&max_tokens=10", "?q=monkey&max_tokens=10")]
    public void MasksCredentialsAndLeavesTheRest(string input, string expected) =>
        Assert.Equal(expected, Redactor.Redact(input));
}

public class BodyLoggingTests
{
    private const string Category = "McpMySite.Server.Requests.BodyLoggingMiddleware";

    private static async Task<WebApplication> StartHostAsync(CapturedLogs logs, Action<WebApplication> map, string? mcpApiKey = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        builder.Services.Configure<TelemetryOptions>(o => o.LogBodies = true);
        builder.Services.Configure<McpOptions>(o => o.ApiKey = mcpApiKey);
        builder.Services.Configure<McpMySite.Core.Embeddings.EmbeddingOptions>(o => o.ApiKeyEnvironmentVariable = "MCPMYSITE_TEST_KEY_THAT_IS_NOT_SET");

        var app = builder.Build();
        app.UseMiddleware<BodyLoggingMiddleware>();
        map(app);
        await app.StartAsync();
        return app;
    }

    private static HttpClient ClientFor(WebApplication app) => new() { BaseAddress = new Uri(app.Urls.First()), Timeout = TimeSpan.FromSeconds(30) };

    [Fact]
    public async Task EventStreamReachesTheClientWhileItIsStillBeingWritten()
    {
        var logs = new CapturedLogs();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = await StartHostAsync(logs, host => host.MapGet("/mcp", async context =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync("data: first-event\n\n");
            await context.Response.Body.FlushAsync();
            await release.Task;
            await context.Response.WriteAsync("data: second-event\n\n");
        }));
        using var client = ClientFor(app);

        using var response = await client.GetAsync("/mcp", HttpCompletionOption.ResponseHeadersRead);
        await using var stream = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[256];
        var read = await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        // The handler is still blocked, so a buffering middleware could not have produced this.
        Assert.Contains("first-event", System.Text.Encoding.UTF8.GetString(buffer, 0, read));
        Assert.Empty(logs.In(Category));

        release.SetResult();
        using var rest = new StreamReader(stream);
        Assert.Contains("second-event", await rest.ReadToEndAsync());

        var entry = Assert.Single(logs.In(Category));
        var body = Assert.IsType<string>(entry["ResponseBody"]);
        Assert.Contains("first-event", body);
        Assert.Contains("second-event", body);
    }

    [Fact]
    public async Task BodiesAreCappedAtEightKilobytes()
    {
        var logs = new CapturedLogs();
        await using var app = await StartHostAsync(logs, host => host.MapPost("/api/search", async context =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();

            // The handler sees the whole body, not the capped copy.
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync($"{{\"received\":{body.Length}}}");
        }));
        using var client = ClientFor(app);

        var payload = "{\"q\":\"" + new string('x', 20_000) + "\"}";
        using var response = await client.PostAsync("/api/search", new StringContent(payload, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal($"{{\"received\":{payload.Length}}}", await response.Content.ReadAsStringAsync());
        var entry = Assert.Single(logs.In(Category));
        Assert.Equal((long)payload.Length, entry["RequestBytes"]);
        var logged = Assert.IsType<string>(entry["RequestBody"]);
        Assert.Equal(BodyLoggingMiddleware.MaxBodyBytes + "[truncated]".Length, logged.Length);
        Assert.EndsWith("[truncated]", logged);
    }

    [Fact]
    public async Task KeysAreMaskedAndOnlyAllowedHeadersAreLogged()
    {
        var logs = new CapturedLogs();
        await using var app = await StartHostAsync(logs, host => host.MapPost("/mcp", async (HttpContext context) =>
        {
            // What was never read is never logged, so the handler has to consume the body as the MCP SDK does.
            using var reader = new StreamReader(context.Request.Body);
            await reader.ReadToEndAsync();
            return Results.Json(new { note = "echo of server-side-key" });
        }), mcpApiKey: "server-side-key");
        using var client = ClientFor(app);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp?api_key=in-the-query&q=visible")
        {
            Content = new StringContent("""{"query":"rockets","password":"hunter2","note":"the key is server-side-key"}""", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Authorization", "Bearer header-secret");
        request.Headers.Add("X-Api-Key", "header-secret-2");
        request.Headers.Add("Cookie", "session=cookie-secret");
        request.Headers.Add("CF-Ray", "8a1b2c3d4e5f6a7b-AMS");
        (await client.SendAsync(request)).EnsureSuccessStatusCode();

        var entry = Assert.Single(logs.In(Category));
        Assert.Equal("?api_key=[redacted]&q=visible", entry["Query"]);

        var requestBody = Assert.IsType<string>(entry["RequestBody"]);
        Assert.Contains("rockets", requestBody);
        Assert.Contains("\"password\":\"[redacted]\"", requestBody);
        Assert.Contains("the key is [redacted]", requestBody);
        Assert.Contains("echo of [redacted]", Assert.IsType<string>(entry["ResponseBody"]));

        var headers = Assert.IsType<string>(entry["RequestHeaders"]);
        Assert.Contains("Content-Type=application/json", headers);
        Assert.Contains("CF-Ray=8a1b2c3d4e5f6a7b-AMS", headers);

        var everything = string.Join('\n', logs.Entries.Select(e => e.Message));
        foreach (var secret in new[] { "header-secret", "cookie-secret", "hunter2", "in-the-query", "server-side-key" })
        {
            Assert.DoesNotContain(secret, everything);
        }
    }

    [Fact]
    public async Task OtherPathsAreNotLogged()
    {
        var logs = new CapturedLogs();
        await using var app = await StartHostAsync(logs, host => host.MapGet("/other", () => "body"));
        using var client = ClientFor(app);

        (await client.GetAsync("/other")).EnsureSuccessStatusCode();

        Assert.Empty(logs.In(Category));
    }
}

public class BodyLoggingConfigurationTests
{
    private const string Category = "McpMySite.Server.Requests.BodyLoggingMiddleware";
    private const string ApiKey = "body-test-key";

    [Fact]
    public async Task BodiesAreNotLoggedByDefault()
    {
        var logs = new CapturedLogs();
        var (factory, client) = await LoggedServer.StartAsync(logs, new() { ["Mcp:ApiKey"] = ApiKey });
        using (factory)
        using (client)
        {
            (await client.GetAsync("/api/search?q=kerosene+zanzibarquery")).EnsureSuccessStatusCode();
            (await LoggedServer.McpAsync(client, LoggedServer.ToolCall("search", new { query = "zanzibarquery" }), ApiKey)).EnsureSuccessStatusCode();
        }

        Assert.Empty(logs.In(Category));
        Assert.DoesNotContain(logs.Entries, e => e.Message.Contains("zanzibarquery", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EnabledLogsSearchAndMcpBodiesWithoutTheKey()
    {
        var logs = new CapturedLogs();
        var (factory, client) = await LoggedServer.StartAsync(logs, new() { ["Mcp:ApiKey"] = ApiKey, ["Telemetry:LogBodies"] = "true" });
        using (factory)
        using (client)
        {
            (await client.GetAsync("/api/search?q=kerosene")).EnsureSuccessStatusCode();
            (await LoggedServer.McpAsync(client, LoggedServer.ToolCall("search", new { query = "kerosene" }), ApiKey)).EnsureSuccessStatusCode();
            (await client.GetAsync("/api/info")).EnsureSuccessStatusCode();
        }

        var bodies = logs.In(Category);
        Assert.Equal(2, bodies.Count);

        var api = Assert.Single(bodies, e => (string?)e["Path"] == "/api/search");
        Assert.Equal("?q=kerosene", api["Query"]);
        Assert.Contains("docs/rockets", Assert.IsType<string>(api["ResponseBody"]));

        var mcp = Assert.Single(bodies, e => (string?)e["Path"] == "/mcp");
        Assert.Contains("kerosene", Assert.IsType<string>(mcp["RequestBody"]));
        Assert.Contains("docs/rockets", Assert.IsType<string>(mcp["ResponseBody"]));

        Assert.DoesNotContain(logs.Entries, e => e.Message.Contains(ApiKey, StringComparison.Ordinal));
    }
}
