namespace McpMySite.Server.Telemetry;

/// <summary>
/// The MCP client behind the request being handled, for the mcp.client.name, mcp.client.version and
/// mcp.protocol.version tags. It is known from initialize, or from every request's _meta on the 2026-07-28 revision.
/// </summary>
public sealed record McpClientTags(string? Name, string? Version, string? ProtocolVersion)
{
    private static readonly AsyncLocal<McpClientTags?> Ambient = new();

    /// <summary>The client of the MCP request being handled on this async flow; null outside one.</summary>
    public static McpClientTags? Current => Ambient.Value;

    public static McpClientTags? From(McpServer server)
    {
        var info = server.ClientInfo;
        var protocolVersion = server.NegotiatedProtocolVersion;
        return info is null && protocolVersion is null ? null : new McpClientTags(info?.Name, info?.Version, protocolVersion);
    }

    /// <summary>
    /// Incoming message filter: tags the SDK's span for the message and keeps the client for the server's own spans and
    /// instruments while the message is handled. The initialize handler is what learns the client, so its span is tagged
    /// again once it has run.
    /// </summary>
    public static McpMessageHandler Filter(McpMessageHandler next) => async (context, cancellationToken) =>
    {
        var client = From(context.Server);
        Ambient.Value = client;
        client?.Apply(Activity.Current);
        try
        {
            await next(context, cancellationToken);
        }
        finally
        {
            From(context.Server)?.Apply(Activity.Current);
            Ambient.Value = null;
        }
    };

    public void Apply(Activity? activity)
    {
        if (activity is null)
        {
            return;
        }

        if (Name is not null)
        {
            activity.SetTag("mcp.client.name", Name);
            activity.SetTag("mcp.client.version", Version);
        }

        if (ProtocolVersion is not null)
        {
            activity.SetTag("mcp.protocol.version", ProtocolVersion);
        }
    }

    public void Apply(ref TagList tags)
    {
        if (Name is not null)
        {
            tags.Add("mcp.client.name", Name);
            tags.Add("mcp.client.version", Version);
        }

        if (ProtocolVersion is not null)
        {
            tags.Add("mcp.protocol.version", ProtocolVersion);
        }
    }
}
