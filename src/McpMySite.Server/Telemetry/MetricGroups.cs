namespace McpMySite.Server.Telemetry;

/// <summary>Groups of metric instruments the server exports; Telemetry:Metrics names the ones to keep.</summary>
[Flags]
public enum MetricGroups
{
    None = 0,

    /// <summary>The server's own instruments (mcpmysite.*), the MCP SDK's (mcp.*) and the embedding calls (gen_ai.*).</summary>
    Server = 1,

    /// <summary>ASP.NET Core: http.server.*, kestrel.*, signalr.*, aspnetcore.*.</summary>
    AspNetCore = 2,

    /// <summary>Outgoing HTTP: http.client.* and dns.*.</summary>
    HttpClient = 4,

    /// <summary>.NET runtime: GC, JIT, thread pool, exceptions (dotnet.*).</summary>
    Runtime = 8,

    All = Server | AspNetCore | HttpClient | Runtime,
}
