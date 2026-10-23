namespace McpMySite.Server.Requests;

// Baseline protections on every response. The pages render an API key field and buttons that call the tools, so a
// hostile site must not be able to show them in an iframe; nosniff and a referrer policy cost nothing and close
// the standard findings. Set before the endpoint runs so the rate limiter's 429 and the transport's answers carry
// them too; an endpoint can still overwrite one. MCP clients ignore all of them.
public sealed class ResponseHeadersMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _contentSecurityPolicy;
    private readonly bool _denyAll;

    public ResponseHeadersMiddleware(RequestDelegate next, IOptions<UiOptions> options)
    {
        _next = next;
        var ancestors = options.Value.FrameAncestors.Where(o => !string.IsNullOrWhiteSpace(o)).Select(o => o.Trim()).ToList();
        _denyAll = ancestors.Count == 0;
        _contentSecurityPolicy = "frame-ancestors " + (_denyAll ? "'none'" : string.Join(' ', ancestors));
    }

    public Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers.ContentSecurityPolicy = _contentSecurityPolicy;
        if (_denyAll)
        {
            // Only for browsers without frame-ancestors; it cannot express an origin list, so it is left out then.
            headers.XFrameOptions = "DENY";
        }

        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        return _next(context);
    }
}
