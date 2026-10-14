namespace McpMySite.Server.Telemetry;

/// <summary>The server's own ActivitySource and Meter, and the instruments recorded on them.</summary>
public static class ServerDiagnostics
{
    /// <summary>Name of both the ActivitySource and the Meter.</summary>
    public const string Name = "McpMySite.Server";

    /// <summary>The MCP SDK's ActivitySource and Meter: one span per JSON-RPC call, tool name included.</summary>
    public const string McpSdkName = "Experimental.ModelContextProtocol";

    public static readonly ActivitySource ActivitySource = new(Name);

    public static readonly Meter Meter = new(Name);

    public static readonly Counter<long> SearchRequests = Meter.CreateCounter<long>(
        "mcpmysite.search.requests", description: "Search requests by channel and fallback state.");

    public static readonly Histogram<double> SearchDuration = Meter.CreateHistogram<double>(
        "mcpmysite.search.duration", unit: "ms", description: "Search latency, embedding included.");

    public static readonly Counter<long> FetchRequests = Meter.CreateCounter<long>(
        "mcpmysite.fetch.requests", description: "Page fetches by channel and whether the page was found.");

    public static readonly Histogram<double> FetchDuration = Meter.CreateHistogram<double>(
        "mcpmysite.fetch.duration", unit: "ms", description: "Page fetch latency.");

    public static readonly Counter<long> IndexLoads = Meter.CreateCounter<long>(
        "mcpmysite.index.loads", description: "Index checks by outcome: loaded, unchanged or failed.");

    public static readonly Histogram<double> IndexLoadDuration = Meter.CreateHistogram<double>(
        "mcpmysite.index.load.duration", unit: "ms", description: "Time to check for, download and open an index.");

    public static readonly Counter<long> RejectedRequests = Meter.CreateCounter<long>(
        "mcpmysite.requests.rejected", description: "Requests turned away before reaching a handler, by reason: api_key or rate_limit.");

    /// <summary>
    /// The URL without its query string, for logs and tags. An index URL can carry a SAS token or other credential
    /// in its query, and that must never reach a log.
    /// </summary>
    public static string Redact(Uri url) => url.GetLeftPart(UriPartial.Path);
}
