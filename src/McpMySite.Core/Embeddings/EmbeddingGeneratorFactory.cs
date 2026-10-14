namespace McpMySite.Core.Embeddings;

public static class EmbeddingGeneratorFactory
{
    /// <summary>
    /// ActivitySource and Meter of the generators built here: Microsoft.Extensions.AI's default, which follows the
    /// OpenTelemetry GenAI conventions (model, token usage, duration). Prompts are never recorded.
    /// </summary>
    public const string TelemetrySourceName = "Experimental.Microsoft.Extensions.AI";

    /// <summary>
    /// Builds an <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> for an OpenAI-compatible endpoint.
    /// <paramref name="model"/> and <paramref name="dimensions"/> override the options; the server passes the
    /// values recorded in the index manifest so queries are embedded exactly like the content was.
    /// </summary>
    public static IEmbeddingGenerator<string, Embedding<float>> Create(EmbeddingOptions options, string? model = null, int? dimensions = null)
    {
        var key = options.ResolveApiKey();
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException(
                $"No embedding API key. Set the {options.ApiKeyEnvironmentVariable} environment variable.");
        }

        var modelId = model ?? options.Model;
        var dims = dimensions ?? options.Dimensions;

        var client = new OpenAIClient(new ApiKeyCredential(key), new OpenAIClientOptions
        {
            Endpoint = new Uri(options.Endpoint.TrimEnd('/')),
        });

        return client
            .GetEmbeddingClient(modelId)
            .AsIEmbeddingGenerator(dims)
            .AsBuilder()
            .ConfigureOptions(o => o.Dimensions ??= dims)
            .UseOpenTelemetry(sourceName: TelemetrySourceName)
            .Build();
    }
}
