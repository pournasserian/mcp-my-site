using System.Text.RegularExpressions;

namespace McpMySite.Server.Requests;

/// <summary>
/// Masks credentials in text that is about to be logged: the server's own keys wherever they appear, and anything
/// that looks like a key, token or password in JSON, a query string or an Authorization header.
/// </summary>
public sealed partial class SecretRedactor(IEnumerable<string?> knownSecrets)
{
    public const string Mask = "[redacted]";

    private readonly string[] _known = knownSecrets.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToArray();

    public string Redact(string text)
    {
        foreach (var secret in _known)
        {
            text = text.Replace(secret, Mask, StringComparison.Ordinal);
        }

        text = JsonSecret().Replace(text, "$1\"" + Mask + "\"");
        text = QuerySecret().Replace(text, "$1" + Mask);
        text = BearerToken().Replace(text, "Bearer " + Mask);
        return KeyLikeToken().Replace(text, Mask);
    }

    // "api_key": "..." and friends. The closing quote is optional so a value cut off by the size cap is still masked.
    [GeneratedRegex("""("[^"\\]*(?:api[_-]?key|authorization|token|secret|passw(?:or)?d|credential|signature)[^"\\]*"\s*:\s*)"(?:[^"\\]|\\.)*"?""", RegexOptions.IgnoreCase)]
    private static partial Regex JsonSecret();

    // ?api_key=..., &token=..., &sig=... (a SAS signature).
    [GeneratedRegex("""([?&;](?:[\w.-]*(?:api[_-]?key|token|secret|passw(?:or)?d|signature)|key|sig)=)[^&;\s"]*""", RegexOptions.IgnoreCase)]
    private static partial Regex QuerySecret();

    [GeneratedRegex(@"\bBearer\s+[A-Za-z0-9._~+/=-]+", RegexOptions.IgnoreCase)]
    private static partial Regex BearerToken();

    // OpenAI-style and OpenRouter keys, which carry no field name that would give them away.
    [GeneratedRegex(@"\bsk-[A-Za-z0-9_-]{16,}")]
    private static partial Regex KeyLikeToken();
}
