using Microsoft.AspNetCore.Routing;

namespace McpMySite.Server.Requests;

public static class HeadSupport
{
    private static readonly HttpMethodMetadata GetAndHead = new(["GET", "HEAD"]);

    // Minimal API routing matches HEAD only against endpoints that declare it, so link checkers, uptime monitors and
    // link unfurlers, which send HEAD first, got 405 from every page. The metadata added last wins, so this turns a
    // GET endpoint into a GET and HEAD one; Kestrel drops the body of a HEAD response itself.
    public static TBuilder AllowHead<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(GetAndHead);
}
