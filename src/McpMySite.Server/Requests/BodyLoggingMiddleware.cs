using Microsoft.AspNetCore.Http.Features;
using System.Text;

namespace McpMySite.Server.Requests;

/// <summary>
/// Telemetry:LogBodies. Logs one line per request to the MCP endpoint and the search API with the headers on an
/// allowlist and the first <see cref="MaxBodyBytes"/> of each body, credentials masked. Requests pass straight
/// through unless Telemetry:LogBodies is on.
/// </summary>
/// <remarks>
/// Nothing is buffered. The request and response streams are wrapped so that what passes through is also copied,
/// up to the cap, and the wrappers hand every read, write and flush straight on. A server-sent event stream
/// therefore reaches the client as it is written, and the log line is written when the response has completed.
/// </remarks>
public sealed class BodyLoggingMiddleware
{
    public const int MaxBodyBytes = 8 * 1024;

    // Captured beyond the cap so that a key cut in two by the cap is still seen whole by the redactor.
    private const int RedactionMargin = 256;

    private static readonly string[] RequestHeaders =
    [
        "Content-Type", "Content-Length", "Accept", "User-Agent", "Origin", "MCP-Protocol-Version", "Mcp-Session-Id",
        "X-Forwarded-For", "CF-Connecting-IP", "CF-Ray", "CF-IPCountry",
    ];

    private static readonly string[] ResponseHeaders = ["Content-Type", "Content-Length", "Cache-Control", "Retry-After", "Mcp-Session-Id"];

    private readonly RequestDelegate _next;
    private readonly ILogger<BodyLoggingMiddleware> _logger;
    private readonly SecretRedactor _redactor;
    private readonly string[] _paths;
    private readonly bool _enabled;

    public BodyLoggingMiddleware(
        RequestDelegate next,
        IOptions<TelemetryOptions> telemetry,
        IOptions<McpOptions> mcp,
        IOptions<SearchApiOptions> searchApi,
        IOptions<EmbeddingOptions> embeddings,
        ILogger<BodyLoggingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
        _enabled = telemetry.Value.LogBodies;
        _paths = searchApi.Value.Enabled ? [mcp.Value.Path, searchApi.Value.Path] : [mcp.Value.Path];
        _redactor = new SecretRedactor([mcp.Value.ApiKey, embeddings.Value.ResolveApiKey()]);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_enabled || !_paths.Any(path => context.Request.Path.StartsWithSegments(path)))
        {
            await _next(context);
            return;
        }

        var request = new Capture();
        var response = new Capture();
        var originalRequestBody = context.Request.Body;
        var originalResponseFeature = context.Features.Get<IHttpResponseBodyFeature>();
        context.Request.Body = new CapturingReadStream(originalRequestBody, request);
        context.Response.Body = new CapturingWriteStream(context.Response.Body, response);
        try
        {
            await _next(context);
        }
        finally
        {
            context.Request.Body = originalRequestBody;
            context.Features.Set(originalResponseFeature);
            Log(context, request, response);
        }
    }

    private void Log(HttpContext context, Capture request, Capture response)
    {
        var http = context.Request;
        _logger.LogInformation(
            "Bodies {Method} {Path}{Query} answered {StatusCode}: request {RequestBytes} bytes, headers {RequestHeaders}, body {RequestBody}; response {ResponseBytes} bytes, headers {ResponseHeaders}, body {ResponseBody}",
            http.Method,
            http.Path.Value,
            _redactor.Redact(http.QueryString.Value ?? string.Empty),
            context.Response.StatusCode,
            request.Total,
            FormatHeaders(http.Headers, RequestHeaders),
            Describe(request, http.ContentType),
            response.Total,
            FormatHeaders(context.Response.Headers, ResponseHeaders),
            Describe(response, context.Response.ContentType));
    }

    private string FormatHeaders(IHeaderDictionary headers, string[] allowed) =>
        string.Join("; ", allowed.Where(headers.ContainsKey).Select(name => $"{name}={_redactor.Redact(headers[name].ToString())}"));

    private string Describe(Capture capture, string? contentType)
    {
        if (capture.Total == 0)
        {
            return string.Empty;
        }

        if (contentType is null || !(contentType.Contains("json", StringComparison.OrdinalIgnoreCase) || contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)))
        {
            return $"[{capture.Total} bytes of {contentType ?? "unknown type"} not logged]";
        }

        var text = _redactor.Redact(Encoding.UTF8.GetString(capture.Bytes));
        return capture.Total > MaxBodyBytes || text.Length > MaxBodyBytes
            ? string.Concat(text.AsSpan(0, Math.Min(text.Length, MaxBodyBytes)), "[truncated]")
            : text;
    }

    private sealed class Capture
    {
        private readonly MemoryStream _bytes = new();

        public long Total { get; private set; }

        public ReadOnlySpan<byte> Bytes => _bytes.GetBuffer().AsSpan(0, (int)_bytes.Length);

        public void Add(ReadOnlySpan<byte> data)
        {
            Total += data.Length;
            var room = MaxBodyBytes + RedactionMargin - (int)_bytes.Length;
            if (room > 0)
            {
                _bytes.Write(data[..Math.Min(room, data.Length)]);
            }
        }
    }

    private sealed class CapturingReadStream(Stream inner, Capture capture) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            capture.Add(buffer.AsSpan(offset, read));
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            capture.Add(buffer[..read]);
            return read;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var read = await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
            capture.Add(buffer.AsSpan(offset, read));
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            capture.Add(buffer.Span[..read]);
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CapturingWriteStream(Stream inner, Capture capture) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            capture.Add(buffer.AsSpan(offset, count));
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            inner.Write(buffer);
            capture.Add(buffer);
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken);
            capture.Add(buffer.AsSpan(offset, count));
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(buffer, cancellationToken);
            capture.Add(buffer.Span);
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
