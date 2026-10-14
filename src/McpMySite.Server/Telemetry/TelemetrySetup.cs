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
                .AddRuntimeInstrumentation());

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

    /// <summary>
    /// Exports what is still buffered once the server has stopped and before its services are disposed. Exporters
    /// create their HTTP client lazily from the container, so an export that first happens during disposal fails,
    /// and the last log records before a shutdown or a crash are the ones most worth having.
    /// </summary>
    public static void FlushTelemetryOnShutdown(this WebApplication app) =>
        app.Lifetime.ApplicationStopped.Register(() =>
        {
            app.Services.GetService<LoggerProvider>()?.ForceFlush();
            app.Services.GetService<TracerProvider>()?.ForceFlush();
            app.Services.GetService<MeterProvider>()?.ForceFlush();
        });
}
