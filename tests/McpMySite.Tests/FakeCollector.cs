using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;

namespace McpMySite.Tests;

/// <summary>
/// Stands in for an OTLP collector (POST /v1/traces, /v1/metrics, /v1/logs over http/protobuf) and for the
/// Application Insights ingestion endpoint (POST /v2.1/track). Names and messages travel as plain UTF-8 inside both
/// payloads, so a test can check what was exported without decoding them.
/// </summary>
public sealed class FakeCollector : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentQueue<(string Path, byte[] Body)> _received = new();

    private FakeCollector(WebApplication app)
    {
        _app = app;
    }

    public string Endpoint => _app.Urls.First();

    /// <summary>An Application Insights connection string that sends to this collector.</summary>
    public string AzureMonitorConnectionString => $"InstrumentationKey=00000000-0000-0000-0000-000000000001;IngestionEndpoint={Endpoint}/";

    public static async Task<FakeCollector> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var collector = new FakeCollector(app);

        app.MapPost("/{**path}", async (HttpRequest request) =>
        {
            using var buffer = new MemoryStream();
            var body = request.Headers.ContentEncoding.ToString().Contains("gzip", StringComparison.OrdinalIgnoreCase)
                ? new GZipStream(request.Body, CompressionMode.Decompress)
                : request.Body;
            await body.CopyToAsync(buffer);
            collector._received.Enqueue((request.Path.Value!, buffer.ToArray()));

            // Application Insights expects a receipt; OTLP accepts any 2xx.
            return request.Path.StartsWithSegments("/v2.1/track")
                ? Results.Json(new { itemsReceived = 1, itemsAccepted = 1, errors = Array.Empty<object>() })
                : Results.Ok();
        });

        await app.StartAsync();
        return collector;
    }

    public IReadOnlyList<string> Paths => _received.Select(r => r.Path).Distinct().ToList();

    /// <summary>Everything received, decoded as text, for failure messages.</summary>
    public string Dump() => string.Join("\n", _received.Select(r => r.Path + " " + Encoding.UTF8.GetString(r.Body)));

    /// <summary>True when something posted to <paramref name="path"/> contains <paramref name="text"/>.</summary>
    public bool Received(string path, string text) =>
        _received.Any(r => r.Path == path && Encoding.UTF8.GetString(r.Body).Contains(text, StringComparison.Ordinal));

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
