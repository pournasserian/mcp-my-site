namespace McpMySite.Core.Telemetry;

/// <summary>Where traces, metrics and logs are exported. The console log is written either way.</summary>
public enum TelemetryExporter
{
    /// <summary>Azure Monitor when its connection string is set, otherwise OTLP when an endpoint is set, otherwise nothing.</summary>
    Auto,

    /// <summary>Console log only.</summary>
    None,

    /// <summary>Application Insights. Needs APPLICATIONINSIGHTS_CONNECTION_STRING.</summary>
    AzureMonitor,

    /// <summary>Any OpenTelemetry collector, configured with the standard OTEL_EXPORTER_OTLP_* settings.</summary>
    Otlp,
}

/// <summary>Picks the exporter both the server and the indexer use, so the same settings behave the same in each.</summary>
public static class TelemetryExport
{
    /// <summary>The Application Insights connection string, which some hosting platforms set automatically.</summary>
    public const string AzureMonitorConnectionStringKey = "APPLICATIONINSIGHTS_CONNECTION_STRING";

    public const string OtlpEndpointKey = "OTEL_EXPORTER_OTLP_ENDPOINT";

    /// <summary>Resolves <see cref="TelemetryExporter.Auto"/> and rejects an explicit choice that cannot work.</summary>
    public static TelemetryExporter Resolve(TelemetryExporter configured, string? azureMonitorConnectionString, string? otlpEndpoint) => configured switch
    {
        TelemetryExporter.Auto when !string.IsNullOrWhiteSpace(azureMonitorConnectionString) => TelemetryExporter.AzureMonitor,
        TelemetryExporter.Auto when !string.IsNullOrWhiteSpace(otlpEndpoint) => TelemetryExporter.Otlp,
        TelemetryExporter.Auto => TelemetryExporter.None,
        TelemetryExporter.AzureMonitor when string.IsNullOrWhiteSpace(azureMonitorConnectionString) =>
            throw new InvalidOperationException($"Telemetry:Exporter is AzureMonitor but {AzureMonitorConnectionStringKey} is not set."),
        _ => configured,
    };
}
