namespace McpMySite.Server.Telemetry;

public static class TelemetrySetup
{
    /// <summary>The instrument groups Telemetry:Metrics can name, in the order the setting lists them by default.</summary>
    public static readonly string[] MetricGroups = ["server", "aspnetcore", "httpclient", "runtime"];

    /// <summary>
    /// OpenTelemetry traces, metrics and logs, exported as Telemetry:Exporter says. The console log is set up by the
    /// host from Logging:Console and is written whatever the exporter.
    /// </summary>
    /// <returns>The exporter in use, with Auto resolved.</returns>
    public static TelemetryExporter AddSiteTelemetry(this WebApplicationBuilder builder)
    {
        var configuration = builder.Configuration;
        var options = configuration.GetSection(TelemetryOptions.SectionName).Get<TelemetryOptions>() ?? new TelemetryOptions();
        var mcp = configuration.GetSection(McpOptions.SectionName).Get<McpOptions>() ?? new McpOptions();
        var connectionString = configuration[TelemetryExport.AzureMonitorConnectionStringKey];
        var exporter = TelemetryExport.Resolve(options.Exporter, connectionString, configuration[TelemetryExport.OtlpEndpointKey]);
        var groups = ParseMetricGroups(options.Metrics);

        var telemetry = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(mcp.ServerName, serviceVersion: typeof(TelemetrySetup).Assembly.GetName().Version?.ToString(3))
                .AddAttributes([new("deployment.environment.name", builder.Environment.EnvironmentName)]))
            .WithTracing(tracing => tracing
                .AddSource(ServerDiagnostics.Name, ServerDiagnostics.McpSdkName, EmbeddingGeneratorFactory.TelemetrySourceName)
                .AddAspNetCoreInstrumentation(o => o.Filter = context => !context.Request.Path.StartsWithSegments("/healthz"))
                .AddHttpClientInstrumentation())
            .WithMetrics(metrics =>
            {
                if (groups.Contains("server"))
                {
                    metrics.AddMeter(ServerDiagnostics.Name, ServerDiagnostics.McpSdkName, EmbeddingGeneratorFactory.TelemetrySourceName);
                }

                if (groups.Contains("aspnetcore"))
                {
                    metrics.AddAspNetCoreInstrumentation();
                }

                if (groups.Contains("httpclient"))
                {
                    metrics.AddHttpClientInstrumentation();
                }

                if (groups.Contains("runtime"))
                {
                    metrics.AddRuntimeInstrumentation();
                }

                // The Azure Monitor distro registers the ASP.NET Core and HttpClient meters itself, so a group that was
                // left out is also dropped by meter name.
                metrics.AddView(instrument => groups.Contains(MetricGroupOf(instrument.Meter.Name)) ? null : MetricStreamConfiguration.Drop);
            });

        if (options.MetricExportIntervalSeconds is { } seconds)
        {
            if (seconds <= 0)
            {
                throw new InvalidOperationException("Telemetry:MetricExportIntervalSeconds must be a positive number of seconds.");
            }

            // The Azure Monitor exporter builds its own metric reader with a fixed interval of 60 s and offers no setting
            // for it, so the choice would be silently ignored there.
            if (exporter == TelemetryExporter.AzureMonitor)
            {
                throw new InvalidOperationException("Telemetry:MetricExportIntervalSeconds is not supported by the AzureMonitor exporter, which exports metrics every 60 seconds; leave it unset or use the Otlp exporter.");
            }

            builder.Services.ConfigureAll<MetricReaderOptions>(o => o.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = seconds * 1000);
        }

        switch (exporter)
        {
            case TelemetryExporter.AzureMonitor:
                // AzureMonitor:* tunes the exporter, for example SamplingRatio, TracesPerSecond or EnableLiveMetrics.
                telemetry.UseAzureMonitor(o =>
                {
                    configuration.GetSection("AzureMonitor").Bind(o);
                    o.ConnectionString = connectionString;
                });
                break;
            case TelemetryExporter.Otlp:
                telemetry.UseOtlpExporter();
                break;
        }

        // Exported log records carry the rendered message, so a collector shows the same text as the console.
        builder.Services.Configure<OpenTelemetryLoggerOptions>(o => o.IncludeFormattedMessage = true);
        return exporter;
    }

    /// <summary>The groups named in Telemetry:Metrics, all of them when it is empty. Rejects a name that is not a group.</summary>
    internal static HashSet<string> ParseMetricGroups(string? setting)
    {
        var groups = (setting ?? string.Empty)
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(g => g.ToLowerInvariant())
            .ToHashSet();
        var unknown = groups.Except(MetricGroups).ToList();
        if (unknown.Count > 0)
        {
            throw new InvalidOperationException($"Telemetry:Metrics names unknown instrument group {string.Join(", ", unknown)}. The groups are {string.Join(", ", MetricGroups)}.");
        }

        return groups.Count == 0 ? [.. MetricGroups] : groups;
    }

    private static string MetricGroupOf(string meterName) => meterName switch
    {
        _ when meterName.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) => "aspnetcore",
        _ when meterName.StartsWith("System.Net.", StringComparison.Ordinal) => "httpclient",
        "System.Runtime" or "OpenTelemetry.Instrumentation.Runtime" => "runtime",
        _ => "server",
    };

    /// <summary>
    /// Exports what is still buffered once the server has stopped and before its services are disposed. Exporters
    /// create their HTTP client lazily from the container, so an export that first happens during disposal fails,
    /// and the last log records before a shutdown or a crash are the ones most worth having.
    /// </summary>
    public static void FlushTelemetryOnShutdown(this WebApplication app) =>
        app.Lifetime.ApplicationStopped.Register(() => FlushTelemetry(app.Services));

    /// <summary>Sends everything the log, trace and metric pipelines hold, and returns once it is sent.</summary>
    public static void FlushTelemetry(IServiceProvider services)
    {
        services.GetService<LoggerProvider>()?.ForceFlush();
        services.GetService<TracerProvider>()?.ForceFlush();
        services.GetService<MeterProvider>()?.ForceFlush();
    }
}
