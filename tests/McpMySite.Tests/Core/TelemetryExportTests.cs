using McpMySite.Core.Telemetry;

namespace McpMySite.Tests.Core;

public class TelemetryExportTests
{
    [Theory]
    [InlineData(TelemetryExporter.Auto, "InstrumentationKey=x", "http://collector:4317", TelemetryExporter.AzureMonitor)]
    [InlineData(TelemetryExporter.Auto, null, "http://collector:4317", TelemetryExporter.Otlp)]
    [InlineData(TelemetryExporter.Auto, " ", null, TelemetryExporter.None)]
    [InlineData(TelemetryExporter.None, "InstrumentationKey=x", "http://collector:4317", TelemetryExporter.None)]
    [InlineData(TelemetryExporter.Otlp, "InstrumentationKey=x", null, TelemetryExporter.Otlp)]
    [InlineData(TelemetryExporter.AzureMonitor, "InstrumentationKey=x", null, TelemetryExporter.AzureMonitor)]
    public void ResolvesTheExporter(TelemetryExporter configured, string? connectionString, string? otlpEndpoint, TelemetryExporter expected)
    {
        Assert.Equal(expected, TelemetryExport.Resolve(configured, connectionString, otlpEndpoint));
    }

    [Fact]
    public void AzureMonitorWithoutConnectionStringIsAnError()
    {
        var error = Assert.Throws<InvalidOperationException>(() => TelemetryExport.Resolve(TelemetryExporter.AzureMonitor, null, null));
        Assert.Contains(TelemetryExport.AzureMonitorConnectionStringKey, error.Message);
    }
}
