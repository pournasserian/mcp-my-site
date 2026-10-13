using System.Threading.RateLimiting;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using McpMySite.Core.Embeddings;
using McpMySite.Server.Api;
using McpMySite.Server.Mcp;
using McpMySite.Server.Search;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.HttpOverrides;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<IndexOptions>(builder.Configuration.GetSection(IndexOptions.SectionName));
builder.Services.Configure<McpOptions>(builder.Configuration.GetSection(McpOptions.SectionName));
builder.Services.Configure<SearchApiOptions>(builder.Configuration.GetSection(SearchApiOptions.SectionName));
builder.Services.Configure<RateLimitOptions>(builder.Configuration.GetSection(RateLimitOptions.SectionName));
builder.Services.Configure<TelemetryOptions>(builder.Configuration.GetSection(TelemetryOptions.SectionName));
builder.Services.Configure<EmbeddingOptions>(builder.Configuration.GetSection(EmbeddingOptions.SectionName));

var mcpOptions = builder.Configuration.GetSection(McpOptions.SectionName).Get<McpOptions>() ?? new McpOptions();
var searchApiOptions = builder.Configuration.GetSection(SearchApiOptions.SectionName).Get<SearchApiOptions>() ?? new SearchApiOptions();
var rateLimitOptions = builder.Configuration.GetSection(RateLimitOptions.SectionName).Get<RateLimitOptions>() ?? new RateLimitOptions();

builder.Services.AddHttpClient("index", client => client.Timeout = TimeSpan.FromMinutes(2));
builder.Services.AddSingleton<IndexHolder>();
builder.Services.AddSingleton<IndexLoader>();
builder.Services.AddHostedService<IndexRefreshService>();
builder.Services.AddSingleton<SiteSearchService>();

// MCP: stateless streamable HTTP, tools only. Instructions come from the loaded index unless overridden.
builder.Services.AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation
        {
            Name = mcpOptions.ServerName,
            Version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
        };
    })
    .WithHttpTransport(options =>
    {
        options.SessionMode = HttpServerSessionMode.Stateless;
        options.ConfigureSessionOptions = (context, serverOptions, _) =>
        {
            var holder = context.RequestServices.GetRequiredService<IndexHolder>();
            serverOptions.ServerInstructions = mcpOptions.Instructions ?? holder.Current?.Manifest.Instructions;
            return Task.CompletedTask;
        };
    })
    .WithTools<SiteTools>();

// CORS policies always exist; an empty origin list simply allows no browser origin.
builder.Services.AddCors();
builder.Services.AddOptions<CorsOptions>().Configure<IOptions<SearchApiOptions>, IOptions<McpOptions>>((cors, searchApi, mcp) =>
{
    cors.AddPolicy(SearchEndpoints.CorsPolicy, policy => policy.WithOrigins(searchApi.Value.AllowedOrigins).WithMethods("GET").AllowAnyHeader());
    cors.AddPolicy(McpCorsPolicy, policy => policy
        .WithOrigins(mcp.Value.AllowedOrigins)
        .WithMethods("GET", "POST", "DELETE")
        .WithHeaders("Content-Type", "Authorization", "X-Api-Key", "MCP-Protocol-Version", "Mcp-Session-Id")
        .WithExposedHeaders("Mcp-Session-Id"));
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(SearchEndpoints.RateLimitPolicy, context =>
    {
        var permit = context.RequestServices.GetRequiredService<IOptions<RateLimitOptions>>().Value.PermitPerMinute;
        return RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = Math.Max(1, permit),
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
    });
});

if (rateLimitOptions.UseForwardedHeaders)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    });
}

// Telemetry: OpenTelemetry everywhere; Application Insights when its connection string is present,
// otherwise OTLP when an endpoint is configured.
var telemetry = builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(mcpOptions.ServerName))
    .WithTracing(tracing => tracing
        .AddSource(SiteSearchService.ActivitySourceName)
        .AddAspNetCoreInstrumentation(o => o.Filter = context => !context.Request.Path.StartsWithSegments("/healthz"))
        .AddHttpClientInstrumentation())
    .WithMetrics(metrics => metrics
        .AddMeter(SiteSearchService.MeterName)
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation());

var applicationInsights = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
if (!string.IsNullOrWhiteSpace(applicationInsights))
{
    telemetry.UseAzureMonitor(o => o.ConnectionString = applicationInsights);
}
else if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    telemetry.UseOtlpExporter();
}

var app = builder.Build();

if (rateLimitOptions.UseForwardedHeaders)
{
    app.UseForwardedHeaders();
}

app.UseCors();
app.UseRateLimiter();

// Optional API key on the MCP endpoint only; the search API is public by design.
app.UseWhen(context => context.Request.Path.StartsWithSegments(mcpOptions.Path), branch => branch.Use(async (context, next) =>
{
    var expected = context.RequestServices.GetRequiredService<IOptions<McpOptions>>().Value.ApiKey;
    if (string.IsNullOrWhiteSpace(expected))
    {
        await next(context);
        return;
    }

    var supplied = context.Request.Headers.Authorization.ToString() is { Length: > 7 } auth && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        ? auth[7..].Trim()
        : context.Request.Headers["X-Api-Key"].ToString();

    if (!CryptographicEquals(supplied, expected))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Bearer";
        await context.Response.WriteAsJsonAsync(new { error = "A valid API key is required." });
        return;
    }

    await next(context);
}));

app.MapMcp(mcpOptions.Path)
    .RequireRateLimiting(SearchEndpoints.RateLimitPolicy)
    .RequireCors(McpCorsPolicy);

if (searchApiOptions.Enabled)
{
    app.MapSearchApi(searchApiOptions);
}

app.MapGet("/healthz", (IndexHolder holder) =>
{
    var manifest = holder.Current?.Manifest;
    var body = new
    {
        status = manifest is null ? "no-index" : "ok",
        site = manifest?.SiteUrl,
        pages = manifest?.PageCount,
        builtAt = manifest?.BuiltAt,
        loadedAt = holder.LoadedAt,
        lastError = holder.LastError,
    };
    return manifest is null ? Results.Json(body, statusCode: StatusCodes.Status503ServiceUnavailable) : Results.Ok(body);
}).ExcludeFromDescription();

app.MapGet("/", (IndexHolder holder) =>
{
    var manifest = holder.Current?.Manifest;
    var name = manifest?.SiteName ?? manifest?.SiteUrl ?? "no index loaded";
    return Results.Text($"mcp-my-site for {name}\nMCP endpoint: {mcpOptions.Path}\nSearch API: {(searchApiOptions.Enabled ? searchApiOptions.Path + "?q=..." : "disabled")}\n");
});

app.Run();

static bool CryptographicEquals(string? a, string b)
{
    if (string.IsNullOrEmpty(a))
    {
        return false;
    }

    var left = System.Text.Encoding.UTF8.GetBytes(a);
    var right = System.Text.Encoding.UTF8.GetBytes(b);
    return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(left, right);
}

public partial class Program
{
    private const string McpCorsPolicy = "mcp";
}
