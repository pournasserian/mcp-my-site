using System.Net;

namespace McpMySite.Server.Requests;

/// <summary>
/// Resolves the IP used to partition the search API's per-IP rate limit. Trusts a forwarded header's address only
/// when the request's own remote address is one of <see cref="SearchApiOptions.TrustedProxies"/> — a narrower,
/// opt-in trust boundary than <see cref="CloudflareClientIpMiddleware"/>, which is for a browser arriving via the
/// Cloudflare edge. This is for a site that calls this API server-to-server (such as a Cloudflare Worker rendering
/// its own search page), where the connecting address is the caller, not the end visitor.
/// </summary>
public sealed class ClientIpResolver
{
    private readonly TrustedNetworks _trusted;
    private readonly string? _header;

    public ClientIpResolver(IOptions<SearchApiOptions> options)
    {
        var value = options.Value;

        // Parsed even when off, so a mistyped entry is found at startup (Program resolves this service once after
        // the host is built) rather than the day the setting is turned on.
        _trusted = TrustedNetworks.Parse(value.TrustedProxies, "SearchApi:TrustedProxies");
        _header = value.ClientIpHeader;
    }

    public string Resolve(HttpContext context)
    {
        var peer = context.Connection.RemoteIpAddress;
        if (_header is { Length: > 0 } header && peer is not null && _trusted.Contains(peer) &&
            context.Request.Headers[header] is { Count: 1 } values && TryParseForwardedAddress(values[0], out var client))
        {
            return client.ToString();
        }

        return peer?.ToString() ?? "unknown";
    }

    // A forwarded-for style header may carry a comma-separated chain; the first entry is the original client.
    private static bool TryParseForwardedAddress(string? value, out IPAddress address) =>
        TrustedNetworks.TryParseAddress(value?.Split(',')[0], out address);
}
