using System.Net;
using System.Net.Sockets;

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
    private readonly System.Net.IPNetwork[] _trusted;
    private readonly string? _header;

    public ClientIpResolver(IOptions<SearchApiOptions> options)
    {
        var value = options.Value;

        // Parsed even when off, so a mistyped entry is found at startup rather than the day the setting is turned on.
        _trusted = value.TrustedProxies.Select(ParseTrustedProxy).ToArray();
        _header = value.ClientIpHeader;
    }

    public string Resolve(HttpContext context)
    {
        var peer = context.Connection.RemoteIpAddress;
        if (_header is { Length: > 0 } header && peer is not null && IsTrusted(peer) &&
            context.Request.Headers[header] is { Count: 1 } values && TryParseForwardedAddress(values[0], out var client))
        {
            return client.ToString();
        }

        return peer?.ToString() ?? "unknown";
    }

    private bool IsTrusted(IPAddress peer)
    {
        var address = peer.IsIPv4MappedToIPv6 ? peer.MapToIPv4() : peer;
        return _trusted.Any(network => network.Contains(address));
    }

    private static System.Net.IPNetwork ParseTrustedProxy(string entry)
    {
        if (System.Net.IPNetwork.TryParse(entry, out var network))
        {
            return network;
        }

        if (IPAddress.TryParse(entry, out var address))
        {
            return new System.Net.IPNetwork(address, address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128);
        }

        throw new InvalidOperationException($"SearchApi:TrustedProxies entry '{entry}' is not an IP address or CIDR range such as 173.245.48.0/20.");
    }

    // A forwarded-for style header may carry a comma-separated chain; the first entry is the original client.
    private static bool TryParseForwardedAddress(string? value, out IPAddress address)
    {
        var first = value?.Split(',')[0].Trim();
        if (string.IsNullOrEmpty(first) || !IPAddress.TryParse(first, out var parsed))
        {
            address = IPAddress.None;
            return false;
        }

        address = parsed;
        return true;
    }
}
