using McpMySite.Core.Telemetry;
using McpMySite.Indexer.Build;
using McpMySite.Indexer.Configuration;
using McpMySite.Indexer.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace McpMySite.Tests.Indexer;

public class IndexerTelemetryTests
{
    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void VerboseTurnsOnDebugOverTheConfiguredDefault()
    {
        // The shipped defaults set Default to Information, which used to hide --verbose entirely.
        var configuration = Configuration(new()
        {
            ["Logging:LogLevel:Default"] = "Information",
            ["Logging:LogLevel:System.Net.Http"] = "Warning",
        });

        using var quiet = IndexerTelemetry.Start(configuration, new TelemetryConfig { Exporter = TelemetryExporter.None }, verbose: false);
        Assert.False(quiet.LoggerFactory.CreateLogger<IndexBuilder>().IsEnabled(LogLevel.Debug));

        using var verbose = IndexerTelemetry.Start(configuration, new TelemetryConfig { Exporter = TelemetryExporter.None }, verbose: true);
        Assert.True(verbose.LoggerFactory.CreateLogger<IndexBuilder>().IsEnabled(LogLevel.Debug));

        // A category the configuration names explicitly keeps its level.
        Assert.False(verbose.LoggerFactory.CreateLogger("System.Net.Http.HttpClient").IsEnabled(LogLevel.Information));
    }

    [Fact]
    public void AutoWithNothingConfiguredExportsNothing()
    {
        using var telemetry = IndexerTelemetry.Start(Configuration([]), new TelemetryConfig(), verbose: false);
        Assert.Equal(TelemetryExporter.None, telemetry.Exporter);
    }

    [Fact]
    public void AzureMonitorWithoutConnectionStringIsRejected()
    {
        Assert.Throws<InvalidOperationException>(() =>
            IndexerTelemetry.Start(Configuration([]), new TelemetryConfig { Exporter = TelemetryExporter.AzureMonitor }, verbose: false));
    }

    [Fact]
    public async Task ExportsTracesMetricsAndLogsOverOtlp()
    {
        await using var collector = await FakeCollector.StartAsync();
        var configuration = Configuration(new()
        {
            [TelemetryExport.OtlpEndpointKey] = collector.Endpoint,
            ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf",
        });

        var telemetry = IndexerTelemetry.Start(configuration, new TelemetryConfig(), verbose: false);
        Assert.Equal(TelemetryExporter.Otlp, telemetry.Exporter);
        EmitBuildSignals(telemetry, "otlp-export.test");

        // Disposing flushes; a CLI run that exits right after must not lose its telemetry.
        telemetry.Dispose();

        Assert.True(collector.Received("/v1/traces", "mcpmysite.build"), $"No build span. Received: {string.Join(", ", collector.Paths)}");
        Assert.True(collector.Received("/v1/metrics", "mcpmysite.build.pages"), "No build metrics.");
        Assert.True(collector.Received("/v1/logs", "otlp-export.test indexed"), "No log record.");
    }

    [Fact]
    public async Task ExportsToApplicationInsights()
    {
        await using var collector = await FakeCollector.StartAsync();
        var configuration = Configuration(new() { [TelemetryExport.AzureMonitorConnectionStringKey] = collector.AzureMonitorConnectionString });

        var telemetry = IndexerTelemetry.Start(configuration, new TelemetryConfig(), verbose: false);
        Assert.Equal(TelemetryExporter.AzureMonitor, telemetry.Exporter);
        EmitBuildSignals(telemetry, "appinsights-export.test");
        telemetry.Dispose();

        // Exact names: the span is "mcpmysite.build"; the metrics only share the prefix.
        Assert.True(collector.Received("/v2.1/track", "\"name\":\"mcpmysite.build\""), $"No build span. {collector.Dump()}");
        Assert.True(collector.Received("/v2.1/track", "\"name\":\"mcpmysite.build.pages\""), "No build metrics.");
        Assert.True(collector.Received("/v2.1/track", "\"message\":\"appinsights-export.test indexed\""), $"No log record. {collector.Dump()}");
    }

    [Fact]
    public async Task FailedBuildIsTracedMeasuredAndLogged()
    {
        const string site = "https://failed-build-telemetry.test/";
        var folder = Path.Combine(Path.GetTempPath(), "mcpmysite-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "site"));
        await File.WriteAllTextAsync(Path.Combine(folder, "site", "index.html"), "<html><body><main><p>Hello.</p></main></body></html>");

        var spans = new List<Activity>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == IndexerDiagnostics.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.GetTagItem("mcpmysite.site") as string == site)
                {
                    lock (spans)
                    {
                        spans.Add(activity);
                    }
                }
            },
        };
        ActivitySource.AddActivityListener(activityListener);

        var durations = new List<KeyValuePair<string, object?>[]>();
        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == IndexerDiagnostics.Name && instrument.Name == "mcpmysite.build.duration")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        meterListener.SetMeasurementEventCallback<double>((_, _, tags, _) =>
        {
            var copy = tags.ToArray();
            if (copy.Any(t => t.Key == "site" && (string?)t.Value == "failed-build-telemetry.test"))
            {
                lock (durations)
                {
                    durations.Add(copy);
                }
            }
        });
        meterListener.Start();

        var logs = new CapturedLogs();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs).SetMinimumLevel(LogLevel.Debug));
        var config = new IndexerConfig();
        config.Site.Url = site;
        config.Source.Type = SourceType.Folder;
        config.Source.Folder = "site";
        config.Cache.Enabled = false;
        config.Embeddings.ApiKeyEnvironmentVariable = "MCPMYSITE_TEST_KEY_THAT_IS_NOT_SET";

        var report = await new IndexBuilder(loggerFactory).RunAsync(config, folder, CancellationToken.None);

        Assert.False(report.Succeeded);
        var build = Assert.Single(spans, s => s.OperationName == "mcpmysite.build");
        Assert.Equal(ActivityStatusCode.Error, build.Status);
        Assert.Equal(false, build.GetTagItem("mcpmysite.succeeded"));
        var tags = Assert.Single(durations);
        Assert.Contains(tags, t => t.Key == "result" && (string?)t.Value == "failed");
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Error && e.Message.StartsWith("Build failed: No embedding API key", StringComparison.Ordinal));
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Information && e.Message.StartsWith("Building a json index of " + site, StringComparison.Ordinal));
    }

    private static void EmitBuildSignals(IndexerTelemetry telemetry, string host)
    {
        using (IndexerDiagnostics.ActivitySource.StartActivity("mcpmysite.build"))
        {
            telemetry.LoggerFactory.CreateLogger<IndexBuilder>().LogInformation("{Site} indexed", host);
        }

        IndexerDiagnostics.RecordBuild(new BuildReport { SiteUrl = $"https://{host}/", Store = "json", Succeeded = true, PagesIndexed = 1 }, "folder", TimeSpan.FromSeconds(1));
    }
}
