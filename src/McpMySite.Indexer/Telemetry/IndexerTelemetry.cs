using Azure.Monitor.OpenTelemetry.Exporter;
using McpMySite.Core.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Console;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace McpMySite.Indexer.Telemetry;

/// <summary>
/// Logging and OpenTelemetry for one indexer run. The console log is configured from Logging (Logging:Console
/// picks the format); traces, metrics and logs are exported as Telemetry:Exporter says. Disposing flushes the
/// exporters, so dispose it before the process exits.
/// </summary>
public sealed class IndexerTelemetry : IDisposable
{
    public const string ServiceName = "mcpmysite-indexer";

    private readonly ServiceProvider _services;

    private IndexerTelemetry(ServiceProvider services, TelemetryExporter exporter)
    {
        _services = services;
        Exporter = exporter;
        LoggerFactory = services.GetRequiredService<ILoggerFactory>();
    }

    public ILoggerFactory LoggerFactory { get; }

    /// <summary>The exporter in use, with Auto resolved.</summary>
    public TelemetryExporter Exporter { get; }

    /// <param name="verbose">Turns Debug on for every category that the configuration does not set more specifically.</param>
    /// <exception cref="InvalidOperationException">The configured exporter cannot work, such as AzureMonitor without a connection string.</exception>
    public static IndexerTelemetry Start(IConfiguration configuration, TelemetryConfig telemetry, bool verbose)
    {
        var connectionString = configuration[TelemetryExport.AzureMonitorConnectionStringKey];
        var exporter = TelemetryExport.Resolve(telemetry.Exporter, connectionString, configuration[TelemetryExport.OtlpEndpointKey]);

        var services = new ServiceCollection();

        // The OpenTelemetry SDK reads its OTEL_* settings from here, so --set and appsettings.json work for them too.
        services.AddSingleton(configuration);
        services.AddLogging(logging =>
        {
            logging.AddConfiguration(configuration.GetSection("Logging"));

            // Defaults for the console; Logging:Console in configuration is applied after these and wins.
            logging.Services.Configure<SimpleConsoleFormatterOptions>(o =>
            {
                o.SingleLine = true;
                o.TimestampFormat = "HH:mm:ss ";
            });
            logging.AddConsole();

            // A rule without a category, added after the configuration's Default, replaces it.
            if (verbose)
            {
                logging.AddFilter(category: null, LogLevel.Debug);
            }
        });

        if (exporter != TelemetryExporter.None)
        {
            // Azure Monitor is added per signal: UseAzureMonitorExporter leaves logs without an exporter outside a host.
            // Its default rate-limited sampler starts with no budget, so it would drop the one trace a build makes,
            // and every log inside it with it; a build is a single trace, so keep all of it.
            var azureMonitor = exporter == TelemetryExporter.AzureMonitor;
            void ConfigureAzureMonitor(AzureMonitorExporterOptions o)
            {
                o.ConnectionString = connectionString;
                o.TracesPerSecond = null;
                o.SamplingRatio = 1.0F;
            }

            var builder = services.AddOpenTelemetry()
                .ConfigureResource(resource => resource.AddService(ServiceName, serviceVersion: typeof(IndexerTelemetry).Assembly.GetName().Version?.ToString(3)))
                .WithTracing(tracing =>
                {
                    tracing.AddSource(IndexerDiagnostics.Name, EmbeddingGeneratorFactory.TelemetrySourceName).AddHttpClientInstrumentation();
                    if (azureMonitor)
                    {
                        tracing.AddAzureMonitorTraceExporter(ConfigureAzureMonitor);
                    }
                })
                .WithMetrics(metrics =>
                {
                    metrics.AddMeter(IndexerDiagnostics.Name, EmbeddingGeneratorFactory.TelemetrySourceName).AddHttpClientInstrumentation();
                    if (azureMonitor)
                    {
                        metrics.AddAzureMonitorMetricExporter(ConfigureAzureMonitor);
                    }
                })
                .WithLogging(logging =>
                {
                    if (azureMonitor)
                    {
                        logging.AddAzureMonitorLogExporter(ConfigureAzureMonitor);
                    }
                });

            if (!azureMonitor)
            {
                builder.UseOtlpExporter();
            }

            // Exported log records carry the rendered message, so a collector shows the same text as the console.
            services.Configure<OpenTelemetryLoggerOptions>(o => o.IncludeFormattedMessage = true);
        }

        var provider = services.BuildServiceProvider();

        // A host would start the providers; without one, resolving them is what subscribes them to the sources.
        provider.GetService<TracerProvider>();
        provider.GetService<MeterProvider>();
        return new IndexerTelemetry(provider, exporter);
    }

    public void Dispose()
    {
        // Flush while the container is still alive. Exporters create their HTTP client lazily from it, so an export
        // that first happens during disposal fails and the batch, often every log record of the run, is lost.
        _services.GetService<LoggerProvider>()?.ForceFlush();
        _services.GetService<TracerProvider>()?.ForceFlush();
        _services.GetService<MeterProvider>()?.ForceFlush();
        _services.Dispose();
    }
}
