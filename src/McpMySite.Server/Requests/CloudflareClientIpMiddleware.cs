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
    private readonly TrustedNetworks _trusted;
    private readonly bool _enabled;

    public CloudflareClientIpMiddleware(RequestDelegate next, IOptions<CloudflareOptions> options)
    {
        _next = next;

        // Parsed even when off, so a mistyped range is found at startup rather than the day the setting is turned on.
        _trusted = TrustedNetworks.Parse(options.Value.TrustedNetworks, "Cloudflare:TrustedNetworks");
        _enabled = options.Value.TrustConnectingIp;
    }

    public Task InvokeAsync(HttpContext context)
    {
        if (_enabled && context.Connection.RemoteIpAddress is { } peer && _trusted.Contains(peer))
        {
            Apply(context);
        }

        return _next(context);
    }

    private static void Apply(HttpContext context)
    {
        var headers = context.Request.Headers;
        var activity = Activity.Current;

        // Exactly one value: a repeated header means something other than Cloudflare added to it.
        if (headers[ConnectingIpHeader] is { Count: 1 } connecting && TrustedNetworks.TryParseAddress(connecting[0], out var client))
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
}
