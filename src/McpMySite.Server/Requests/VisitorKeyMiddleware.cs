using System.Security.Cryptography;
using System.Text;

namespace McpMySite.Server.Requests;

/// <summary>
/// Opt-in (Telemetry:VisitorId) key for counting distinct visitors without storing addresses: SHA-256 of the client
/// address, the user agent and a salt that is random, kept in memory only and replaced at midnight UTC. The key cannot
/// be reversed and cannot link one day to the next, so it answers "distinct visitors today", not who they were.
/// </summary>
public sealed class VisitorKeyMiddleware(RequestDelegate next, IOptions<TelemetryOptions> options, VisitorSalt salt, TimeProvider time)
{
    public const string Tag = "mcpmysite.visitor";

    private readonly bool _enabled = options.Value.VisitorId;

    public Task InvokeAsync(HttpContext context)
    {
        if (_enabled && context.Connection.RemoteIpAddress is { } address)
        {
            var key = VisitorKey.Compute(salt.Get(time.GetUtcNow()), address.ToString(), context.Request.Headers.UserAgent.ToString());
            VisitorKey.Current = key;
            Activity.Current?.SetTag(Tag, key);
        }

        return next(context);
    }
}

public static class VisitorKey
{
    private static readonly AsyncLocal<string?> Ambient = new();

    /// <summary>The key of the request being handled on this async flow; null when the setting is off or outside a request.</summary>
    public static string? Current
    {
        get => Ambient.Value;
        internal set => Ambient.Value = value;
    }

    public static string Compute(ReadOnlySpan<byte> salt, string address, string userAgent)
    {
        var input = Encoding.UTF8.GetBytes($"{address}\n{userAgent}");
        var buffer = new byte[salt.Length + input.Length];
        salt.CopyTo(buffer);
        input.CopyTo(buffer, salt.Length);
        return Convert.ToHexStringLower(SHA256.HashData(buffer))[..16];
    }
}

public sealed class VisitorSalt
{
    private readonly Lock _gate = new();
    private DateOnly _day;
    private byte[] _salt = [];

    public byte[] Get(DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        lock (_gate)
        {
            if (today != _day)
            {
                _day = today;
                _salt = RandomNumberGenerator.GetBytes(32);
            }

            return _salt;
        }
    }
}
