namespace McpMySite.Core.Embeddings;

/// <summary>
/// Connection settings for any OpenAI-compatible embeddings endpoint (gateway or provider). OpenRouter is the default.
/// The key itself is never stored in config files; it is read from the named environment variable.
/// </summary>
public sealed class EmbeddingOptions
{
    public const string SectionName = "Embeddings";

    /// <summary>Base URL of the OpenAI-compatible API, including the version segment.</summary>
    public string Endpoint { get; set; } = "https://openrouter.ai/api/v1";

    public string Model { get; set; } = "openai/text-embedding-3-small";

    public int Dimensions { get; set; } = 512;

    /// <summary>Environment variable that holds the API key.</summary>
    public string ApiKeyEnvironmentVariable { get; set; } = "AI_API_KEY";

    /// <summary>Direct key value; only meant for user-secrets or tests. Takes precedence when set.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Texts per request when embedding in bulk.</summary>
    public int BatchSize { get; set; } = 64;

    public string? ResolveApiKey() =>
        !string.IsNullOrWhiteSpace(ApiKey) ? ApiKey : Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable);
}
