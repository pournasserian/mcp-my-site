extern alias server;
using McpMySite.Server.Index;
using McpMySite.Server.Options;
using McpMySite.Server.Search;
using McpMySite.Server.Telemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;

namespace McpMySite.Tests.Server;

/// <summary>Starts the real server with its shipped logging configuration and keeps every log record.</summary>
internal static class LoggedServer
{
    public static async Task<(WebApplicationFactory<server::Program> Factory, HttpClient Client)> StartAsync(
        CapturedLogs logs, Dictionary<string, string?> settings, Dictionary<string, string?>? hostSettings = null, Action<IServiceCollection>? configureServices = null)
    {
        var index = await TestIndex.WriteAsync(IndexFormat.JsonStoreType);
        var factory = new WebApplicationFactory<server::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");

            // Host settings are visible while Program runs, which is when the exporter is chosen.
            foreach (var (key, value) in hostSettings ?? [])
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>(settings)
            {
                ["Index:Path"] = index,
                ["Embeddings:ApiKeyEnvironmentVariable"] = "MCPMYSITE_TEST_KEY_THAT_IS_NOT_SET",
            }));
            builder.ConfigureLogging(logging => logging.AddProvider(logs));
            builder.ConfigureServices(services => configureServices?.Invoke(services));
        });

        var client = factory.CreateClient();
        for (var i = 0; i < 100; i++)
        {
            if ((await client.GetAsync("/healthz")).IsSuccessStatusCode)
            {
                return (factory, client);
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("Index did not load.");
    }

    public static async Task<HttpResponseMessage> McpAsync(HttpClient client, object request, string? apiKey = null)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = JsonContent.Create(request) };
        message.Headers.Accept.ParseAdd("application/json");
        message.Headers.Accept.ParseAdd("text/event-stream");
        if (apiKey is not null)
        {
            message.Headers.Add("X-Api-Key", apiKey);
        }

        return await client.SendAsync(message);
    }

    public static object ToolCall(string name, object arguments) =>
        new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name, arguments } };
}

public sealed class ServerLoggingTests : IAsyncLifetime
{
    private const string ApiKey = "logging-test-key";
    private readonly CapturedLogs _logs = new();
    private WebApplicationFactory<server::Program> _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync() =>
        (_factory, _client) = await LoggedServer.StartAsync(_logs, new() { ["Mcp:ApiKey"] = ApiKey, ["RateLimiting:PermitPerMinute"] = "1000" });

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task SearchIsLoggedWithoutItsQueryText()
    {
        (await _client.GetAsync("/api/search?q=kerosene+zanzibarquery&limit=3")).EnsureSuccessStatusCode();

        var search = Assert.Single(_logs.In("McpMySite.Server.Search.SiteSearchService"), e => e.Message.StartsWith("Search (api)", StringComparison.Ordinal));
        Assert.Equal(1, search["Hits"]);
        Assert.Equal(3, search["Limit"]);
        Assert.Equal(true, search["Fallback"]);

        var request = Assert.Single(_logs.In("Microsoft.AspNetCore.HttpLogging.HttpLoggingMiddleware"), e => e.Message.Contains("/api/search", StringComparison.Ordinal));
        Assert.Contains("StatusCode: 200", request.Message);

        // Neither the search line nor the request line, nor anything else, may carry the query.
        Assert.DoesNotContain(_logs.Entries, e => e.Message.Contains("zanzibarquery", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HealthChecksAreNotRequestLogged()
    {
        (await _client.GetAsync("/healthz")).EnsureSuccessStatusCode();
        (await _client.GetAsync("/api/info")).EnsureSuccessStatusCode();

        var requests = _logs.In("Microsoft.AspNetCore.HttpLogging.HttpLoggingMiddleware");
        Assert.Contains(requests, e => e.Message.Contains("/api/info", StringComparison.Ordinal));
        Assert.DoesNotContain(requests, e => e.Message.Contains("/healthz", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RejectedApiKeysAreLoggedAsMissingOrInvalid()
    {
        var list = new { jsonrpc = "2.0", id = 1, method = "tools/list" };
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoggedServer.McpAsync(_client, list)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoggedServer.McpAsync(_client, list, "wrong-key")).StatusCode);

        var rejections = _logs.In("McpMySite.Server.Requests").Where(e => e.Level == LogLevel.Warning).Select(e => e.Message).ToList();
        Assert.Contains("Rejected POST /mcp: missing API key", rejections);
        Assert.Contains("Rejected POST /mcp: invalid API key", rejections);
        Assert.DoesNotContain(_logs.Entries, e => e.Message.Contains("wrong-key", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FetchOfAnUnknownPageDoesNotLogWhatWasAskedFor()
    {
        var response = await LoggedServer.McpAsync(_client, LoggedServer.ToolCall("fetch", new { url = "https://example.test/private-guess" }), ApiKey);
        response.EnsureSuccessStatusCode();

        Assert.Contains(_logs.In("McpMySite.Server.Search.SiteSearchService"), e => e.Message == "Fetch (mcp): no such page");

        // The MCP SDK logs the tool error with its message, which names the URL; that is outside this server's control.
        Assert.DoesNotContain(_logs.Entries.Where(e => e.Category.StartsWith("McpMySite", StringComparison.Ordinal)), e => e.Message.Contains("private-guess", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FetchOfAKnownPageIsLoggedWithItsUrl()
    {
        var response = await LoggedServer.McpAsync(_client, LoggedServer.ToolCall("fetch", new { url = "https://example.test/docs/widgets" }), ApiKey);
        response.EnsureSuccessStatusCode();

        Assert.Contains(_logs.In("McpMySite.Server.Search.SiteSearchService"), e => e.Message.StartsWith("Fetch (mcp) https://example.test/docs/widgets in ", StringComparison.Ordinal));
    }

    [Fact]
    public void StartupLineDescribesTheConfiguration()
    {
        var startup = Assert.Single(_logs.In("McpMySite.Server.Startup"), e => e.Level == LogLevel.Information);
        Assert.Contains("MCP at /mcp (API key required)", startup.Message);
        Assert.Contains("telemetry export None", startup.Message);
        Assert.Contains("query text logging off", startup.Message);

        // No embedding key in tests, which the server calls out once at startup.
        Assert.Contains(_logs.In("McpMySite.Server.Startup"), e => e.Level == LogLevel.Warning && e.Message.Contains("MCPMYSITE_TEST_KEY_THAT_IS_NOT_SET", StringComparison.Ordinal));
    }

    [Fact]
    public void IndexLoadIsLogged()
    {
        var loaded = Assert.Single(_logs.In("McpMySite.Server.Index.IndexLoader"), e => e.Message.StartsWith("Loaded json index", StringComparison.Ordinal));
        Assert.Equal(3, loaded["Pages"]);
        Assert.Contains(_logs.In("McpMySite.Server.Index.IndexRefreshService"), e => e.Message.StartsWith("Loading the index from", StringComparison.Ordinal));
    }
}

public class RateLimitLoggingTests
{
    [Fact]
    public async Task RejectedRequestsAreLogged()
    {
        var logs = new CapturedLogs();
        var (factory, client) = await LoggedServer.StartAsync(logs, new() { ["RateLimiting:PermitPerMinute"] = "1" });
        using (factory)
        using (client)
        {
            await client.GetAsync("/api/search?q=rockets");
            var second = await client.GetAsync("/api/search?q=rockets");
            Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        }

        Assert.Contains(logs.In("McpMySite.Server.Requests"), e => e.Level == LogLevel.Warning && e.Message == "Rate limit exceeded: GET /api/search answered 429");
    }
}

public class ServerExportTests
{
    [Fact]
    public async Task ExportsTracesMetricsAndLogsOverOtlp()
    {
        await using var collector = await FakeCollector.StartAsync();
        var (factory, client) = await LoggedServer.StartAsync(new CapturedLogs(), [], new()
        {
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = collector.Endpoint,
            ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf",
        });

        using (factory)
        using (client)
        {
            await ExerciseAsync(factory, client);
        }

        Assert.True(collector.Received("/v1/traces", "mcpmysite.search"), $"No search span. Received: {string.Join(", ", collector.Paths)}");
        Assert.True(collector.Received("/v1/traces", "tools/call"), "No MCP SDK span.");
        Assert.True(collector.Received("/v1/metrics", "mcpmysite.search.requests"), "No search metric.");
        Assert.True(collector.Received("/v1/metrics", "mcpmysite.index.age"), "No index age gauge.");
        Assert.True(collector.Received("/v1/logs", "Search (api) ->"), "No search log.");
        Assert.True(collector.Received("/v1/logs", "Loading the index from"), "No startup log.");
        Assert.True(collector.Received("/v1/logs", "telemetry export Otlp"), "No startup summary.");
    }

    [Fact]
    public async Task ExportsToApplicationInsights()
    {
        await using var collector = await FakeCollector.StartAsync();
        var (factory, client) = await LoggedServer.StartAsync(new CapturedLogs(), [], new()
        {
            ["APPLICATIONINSIGHTS_CONNECTION_STRING"] = collector.AzureMonitorConnectionString + $";LiveEndpoint={collector.Endpoint}/",
            ["AzureMonitor:EnableLiveMetrics"] = "false",

            // The documented way to keep every trace; the default rate limit could drop the few a test makes.
            ["AzureMonitor:TracesPerSecond"] = "",
            ["AzureMonitor:SamplingRatio"] = "1",
        });

        using (factory)
        using (client)
        {
            await ExerciseAsync(factory, client);
        }

        Assert.True(collector.Received("/v2.1/track", "\"name\":\"mcpmysite.search\""), $"No search span. Received: {string.Join(", ", collector.Paths)}");
        Assert.True(collector.Received("/v2.1/track", "\"name\":\"mcpmysite.search.requests\""), "No search metric.");
        Assert.True(collector.Received("/v2.1/track", "\"message\":\"Search (api) -"), "No search log.");
        Assert.True(collector.Received("/v2.1/track", "Loading the index from"), "No startup log.");
        Assert.True(collector.Received("/v2.1/track", "telemetry export AzureMonitor"), "No startup summary.");
    }

    [Fact]
    public async Task ShutdownFlushSendsWhatIsBuffered()
    {
        await using var collector = await FakeCollector.StartAsync();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = collector.Endpoint,
            ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf",
        });
        builder.AddSiteTelemetry();
        var app = builder.Build();
        app.FlushTelemetryOnShutdown();

        await app.StartAsync();
        app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ShutdownTest").LogWarning("Written just before shutdown");
        await app.StopAsync();
        await app.DisposeAsync();

        // Without the flush on stop, the record would still be batched when the container goes, and be lost.
        Assert.True(collector.Received("/v1/logs", "Written just before shutdown"), $"Not exported. Received: {string.Join(", ", collector.Paths)}");
    }

    private static async Task ExerciseAsync(WebApplicationFactory<server::Program> factory, HttpClient client)
    {
        (await client.GetAsync("/api/search?q=rockets")).EnsureSuccessStatusCode();
        (await LoggedServer.McpAsync(client, LoggedServer.ToolCall("search", new { query = "widgets" }))).EnsureSuccessStatusCode();

        // Flushed here rather than left to the shutdown flush: disposing a WebApplicationFactory tears the container
        // down from the test thread while the server's own shutdown runs on another, which a real exit never does.
        // ShutdownFlushSendsWhatIsBuffered covers the shutdown path on a host stopped the way the server stops.
        TelemetrySetup.FlushTelemetry(factory.Services);
    }
}

public class ServerTelemetryUnitTests
{
    private static async Task<IndexHolder> LoadedHolderAsync(IMeterFactory? meterFactory = null)
    {
        var holder = new IndexHolder(meterFactory);
        holder.Swap(await IndexStoreOpener.OpenAsync(await TestIndex.WriteAsync(IndexFormat.JsonStoreType, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero))));
        return holder;
    }

    private static SiteSearchService Search(IndexHolder holder, CapturedLogs logs, bool logQueryText = false) => new(
        holder,
        Options.Create(new McpMySite.Core.Embeddings.EmbeddingOptions { ApiKeyEnvironmentVariable = "MCPMYSITE_TEST_KEY_THAT_IS_NOT_SET" }),
        Options.Create(new TelemetryOptions { LogQueryText = logQueryText }),
        logs.For<SiteSearchService>());

    [Fact]
    public async Task EmbeddingFailureWarnsOncePerOutage()
    {
        var logs = new CapturedLogs();
        using var search = Search(await LoadedHolderAsync(), logs);

        await search.SearchAsync("rockets", 5, "api", CancellationToken.None);
        await search.SearchAsync("widgets", 5, "api", CancellationToken.None);
        await search.SearchAsync("gears", 5, "mcp", CancellationToken.None);

        Assert.Single(logs.Entries, e => e.Level == LogLevel.Warning);
        Assert.Equal(2, logs.Entries.Count(e => e.Level == LogLevel.Debug && e.Message.StartsWith("Embedding failed again", StringComparison.Ordinal)));
        Assert.Equal(3, logs.Entries.Count(e => e.Message.Contains("keyword fallback True", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryTextIsLoggedOnlyWhenAllowed(bool logQueryText)
    {
        var logs = new CapturedLogs();
        using var search = Search(await LoadedHolderAsync(), logs, logQueryText);

        await search.SearchAsync("kerosene", 5, "api", CancellationToken.None);
        await search.FetchAsync("https://example.test/guessed-page", "mcp", CancellationToken.None);

        Assert.Equal(logQueryText, logs.Entries.Any(e => e.Message.Contains("kerosene", StringComparison.Ordinal)));
        Assert.Equal(logQueryText, logs.Entries.Any(e => e.Message.Contains("guessed-page", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task SearchWithoutAnIndexIsLoggedAndRefused()
    {
        var logs = new CapturedLogs();
        using var search = Search(new IndexHolder(), logs);

        await Assert.ThrowsAsync<IndexUnavailableException>(() => search.SearchAsync("rockets", 5, "api", CancellationToken.None));

        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Message.StartsWith("Search (api) refused: No index is loaded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task IndexHolderPublishesAgeAndPages()
    {
        using var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var factory = services.GetRequiredService<IMeterFactory>();
        var holder = await LoadedHolderAsync(factory);

        var values = new Dictionary<string, double>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == ServerDiagnostics.Name && instrument.Meter.Scope == factory)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, _, _) => values[instrument.Name] = value);
        listener.SetMeasurementEventCallback<int>((instrument, value, _, _) => values[instrument.Name] = value);
        listener.Start();
        listener.RecordObservableInstruments();

        Assert.Equal(3, values["mcpmysite.index.pages"]);
        Assert.True(values["mcpmysite.index.age"] > TimeSpan.FromDays(30).TotalSeconds);
        GC.KeepAlive(holder);
    }

    [Fact]
    public async Task IndexUrlQueryStringNeverReachesTheLog()
    {
        var directory = await TestIndex.WriteAsync(IndexFormat.JsonStoreType);
        var host = WebApplication.CreateBuilder();
        host.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = host.Build();
        app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(directory), ServeUnknownFileTypes = true });
        await app.StartAsync();

        var logs = new CapturedLogs();
        var http = new ServiceCollection().AddHttpClient().BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
        var holder = new IndexHolder();
        var options = new IndexOptions { Url = app.Urls.First() + "/manifest.json?sv=2026&sig=TOPSECRETSIGNATURE" };
        var loader = new IndexLoader(holder, Options.Create(options), http, logs.For<IndexLoader>());

        Assert.True(await loader.RefreshAsync(CancellationToken.None));
        Assert.DoesNotContain("TOPSECRETSIGNATURE", loader.Source);
        Assert.Contains(logs.Entries, e => e.Message.StartsWith("Loaded json index from " + app.Urls.First() + "/manifest.json in ", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Entries, e => e.Message.Contains("TOPSECRETSIGNATURE", StringComparison.Ordinal));

        await app.StopAsync();
    }
}
