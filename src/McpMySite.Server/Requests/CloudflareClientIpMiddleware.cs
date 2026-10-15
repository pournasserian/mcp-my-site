using System.Net;
using System.Net.Sockets;

namespace McpMySite.Server.Requests;

/// <summary>
/// Behind Cloudflare the caller of the server is a Cloudflare edge address, and the visitor is in CF-Connecting-IP.
/// The header is believed only when the caller is inside the Cloudflare ranges, since anyone else could send it.
/// The resolved address becomes the connection's remote address, so everything after this point, the per-IP rate
/// limiter included, sees the visitor. The Cloudflare request attributes are added to the request's trace so a row
/// in Application Insights can be matched to the same request in the Cloudflare dashboards by Ray ID.
/// </summary>
public sealed class CloudflareClientIpMiddleware
{
    public const string ConnectingIpHeader = "CF-Connecting-IP";
    public const string RayHeader = "CF-Ray";
    public const string CountryHeader = "CF-IPCountry";

    public const string RayTag = "cloudflare.ray_id";
    public const string CountryTag = "cloudflare.country";

    /// <summary>OpenTelemetry semantic convention for the address of the client that made the request.</summary>
    public const string ClientAddressTag = "client.address";

    private readonly RequestDelegate _next;
    private readonly System.Net.IPNetwork[] _trusted;
    private readonly bool _enabled;

    public CloudflareClientIpMiddleware(RequestDelegate next, IOptions<CloudflareOptions> options)
    {
        _next = next;

        // Parsed even when off, so a mistyped range is found at startup rather than the day the setting is turned on.
        _trusted = options.Value.TrustedNetworks.Select(range =>
            System.Net.IPNetwork.TryParse(range, out var network)
                ? network
                : throw new InvalidOperationException($"Cloudflare:TrustedNetworks entry '{range}' is not a CIDR range such as 173.245.48.0/20.")).ToArray();
        _enabled = options.Value.TrustConnectingIp;
    }

    public Task InvokeAsync(HttpContext context)
    {
        if (_enabled && context.Connection.RemoteIpAddress is { } peer && IsTrusted(peer))
        {
            Apply(context);
        }

        return _next(context);
    }

    private bool IsTrusted(IPAddress peer)
    {
        var address = peer.IsIPv4MappedToIPv6 ? peer.MapToIPv4() : peer;
        return _trusted.Any(network => network.Contains(address));
    }

    private static void Apply(HttpContext context)
    {
        var headers = context.Request.Headers;
        var activity = Activity.Current;

        // Exactly one value: a repeated header means something other than Cloudflare added to it.
        if (headers[ConnectingIpHeader] is { Count: 1 } connecting && TryParseAddress(connecting[0], out var client))
        {
            context.Connection.RemoteIpAddress = client;
            activity?.SetTag(ClientAddressTag, client.ToString());
        }

        // The values go into telemetry, so anything that does not look like what Cloudflare sends is dropped.
        if (headers[RayHeader] is { Count: 1 } ray && ray[0] is { Length: > 0 and <= 40 } rayId && rayId.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
        {
            activity?.SetTag(RayTag, rayId);
        }

        if (headers[CountryHeader] is { Count: 1 } country && country[0] is { Length: 2 } code && code.All(char.IsAsciiLetterOrDigit))
        {
            activity?.SetTag(CountryTag, code);
        }
    }

    // IPAddress.TryParse also accepts "1" and "1.2" as shorthand; an address from Cloudflare is always written in full.
    private static bool TryParseAddress(string? value, out IPAddress address)
    {
        address = IPAddress.None;
        if (string.IsNullOrWhiteSpace(value) || !IPAddress.TryParse(value.Trim(), out var parsed))
        {
            return false;
        }

        var complete = parsed.AddressFamily switch
        {
            AddressFamily.InterNetwork => value.Count(c => c == '.') == 3,
            AddressFamily.InterNetworkV6 => value.Contains(':'),
            _ => false,
        };
        if (complete)
        {
            address = parsed;
        }

        return complete;
    }
}
