namespace McpMySite.Server.Telemetry;

public static class TelemetrySetup
{
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

        var telemetry = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(mcp.ServerName, serviceVersion: typeof(TelemetrySetup).Assembly.GetName().Version?.ToString(3))
                .AddAttributes([new("deployment.environment.name", builder.Environment.EnvironmentName)]))
            .WithTracing(tracing => tracing
                .AddSource(ServerDiagnostics.Name, ServerDiagnostics.McpSdkName, EmbeddingGeneratorFactory.TelemetrySourceName)
                .AddAspNetCoreInstrumentation(o => o.Filter = context => !context.Request.Path.StartsWithSegments("/healthz"))
                .AddHttpClientInstrumentation())
            .WithMetrics(metrics => metrics
                .AddMeter(ServerDiagnostics.Name, ServerDiagnostics.McpSdkName, EmbeddingGeneratorFactory.TelemetrySourceName)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .DropMetricGroupsNotIn(options.Metrics));

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

    private static readonly (MetricGroups Group, string[] Instruments)[] GroupInstruments =
    [
        (MetricGroups.Server, ["mcpmysite.*", "mcp.*", "gen_ai.*"]),
        (MetricGroups.AspNetCore, ["http.server.*", "kestrel.*", "signalr.*", "aspnetcore.*"]),
        (MetricGroups.HttpClient, ["http.client.*", "dns.*"]),
        (MetricGroups.Runtime, ["dotnet.*", "process.runtime.dotnet.*"]),
    ];

    /// <summary>
    /// Drops the instruments of every group not in <paramref name="enabled"/>. Done with views rather than by leaving
    /// the instrumentation out, because the Azure Monitor distro enables the HTTP server and client meters on its own.
    /// </summary>
    private static MeterProviderBuilder DropMetricGroupsNotIn(this MeterProviderBuilder metrics, MetricGroups enabled)
    {
        foreach (var (group, instruments) in GroupInstruments)
        {
            if (enabled.HasFlag(group))
            {
                continue;
            }

            foreach (var instrument in instruments)
            {
                metrics.AddView(instrument, MetricStreamConfiguration.Drop);
            }
        }

        return metrics;
    }

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
