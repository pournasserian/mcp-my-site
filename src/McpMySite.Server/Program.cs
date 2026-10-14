var builder = WebApplication.CreateBuilder(args);
var services = builder.Services;
var configuration = builder.Configuration;

services.Configure<IndexOptions>(configuration.GetSection(IndexOptions.SectionName));
services.Configure<McpOptions>(configuration.GetSection(McpOptions.SectionName));
services.Configure<SearchApiOptions>(configuration.GetSection(SearchApiOptions.SectionName));
services.Configure<RateLimitOptions>(configuration.GetSection(RateLimitOptions.SectionName));
services.Configure<TelemetryOptions>(configuration.GetSection(TelemetryOptions.SectionName));
services.Configure<UiOptions>(configuration.GetSection(UiOptions.SectionName));
services.Configure<EmbeddingOptions>(configuration.GetSection(EmbeddingOptions.SectionName));

var mcpOptions = configuration.GetSection(McpOptions.SectionName).Get<McpOptions>() ?? new McpOptions();
var searchApiOptions = configuration.GetSection(SearchApiOptions.SectionName).Get<SearchApiOptions>() ?? new SearchApiOptions();
var rateLimitOptions = configuration.GetSection(RateLimitOptions.SectionName).Get<RateLimitOptions>() ?? new RateLimitOptions();
var uiOptions = configuration.GetSection(UiOptions.SectionName).Get<UiOptions>() ?? new UiOptions();

services.AddHttpClient("index", client => client.Timeout = TimeSpan.FromMinutes(2));
services.AddSingleton<IndexHolder>();
services.AddSingleton<IndexLoader>();
services.AddHostedService<IndexRefreshService>();
services.AddSingleton<SiteSearchService>();
services.AddSiteOpenApi();
services.AddRazorComponents();
services.AddSingleton<LlmsTxt>();

// Numbers are plain JSON numbers; the web default also accepts strings, which would make every OpenAPI number "integer or string".
services.ConfigureHttpJsonOptions(options => options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict);

// MCP: stateless streamable HTTP, tools only. Instructions come from the loaded index unless overridden.
services.AddMcpServer(options =>
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
    .WithTools<SiteTools>()
    .WithRequestFilters(filters => filters.AddListToolsFilter(next => async (context, cancellationToken) =>
        SiteTools.DescribeSite(await next(context, cancellationToken), context.Services?.GetService<IndexHolder>()?.Current?.Manifest)));

// CORS policies always exist; an empty origin list simply allows no browser origin.
services.AddCors();
services.AddOptions<CorsOptions>().Configure<IOptions<SearchApiOptions>, IOptions<McpOptions>>((cors, searchApi, mcp) =>
{
    cors.AddPolicy(SearchEndpoints.CorsPolicy, policy => policy.WithOrigins(searchApi.Value.AllowedOrigins).WithMethods("GET").AllowAnyHeader());
    cors.AddPolicy(McpCorsPolicy, policy => policy
        .WithOrigins(mcp.Value.AllowedOrigins)
        .WithMethods("GET", "POST", "DELETE")
        .WithHeaders("Content-Type", "Authorization", "X-Api-Key", "MCP-Protocol-Version", "Mcp-Session-Id")
        .WithExposedHeaders("Mcp-Session-Id"));
});

services.AddRateLimiter(options =>
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
    services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    });
}

// Telemetry: OpenTelemetry everywhere; Application Insights when its connection string is present,
// otherwise OTLP when an endpoint is configured.
var telemetry = services.AddOpenTelemetry()
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

var applicationInsights = configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
if (!string.IsNullOrWhiteSpace(applicationInsights))
{
    telemetry.UseAzureMonitor(o => o.ConnectionString = applicationInsights);
}
else if (!string.IsNullOrWhiteSpace(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
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

app.MapSiteOpenApi(uiOptions);
app.MapInfoApi();
app.MapPages(uiOptions);

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

// With the pages on, / is the landing page; without them it stays a short text summary.
if (!uiOptions.Enabled)
{
    app.MapGet("/", (IndexHolder holder) =>
    {
        var manifest = holder.Current?.Manifest;
        var name = manifest?.SiteName ?? manifest?.SiteUrl ?? "no index loaded";
        return Results.Text($"mcp-my-site for {name}\nMCP endpoint: {mcpOptions.Path}\nSearch API: {(searchApiOptions.Enabled ? searchApiOptions.Path + "?q=..." : "disabled")}\n");
    }).ExcludeFromDescription();
}

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
