using Microsoft.Extensions.AI;

namespace McpMySite.Indexer.Embeddings;

/// <summary>Embeds texts in batches, serving unchanged texts from the cache and retrying transient provider errors.</summary>
public sealed class BatchEmbedder(
    IEmbeddingGenerator<string, Embedding<float>> generator,
    EmbeddingCache? cache,
    string model,
    int dimensions,
    int batchSize,
    ILogger logger)
{
    public int CacheHits { get; private set; }

    public int Embedded { get; private set; }

    /// <summary>Returns a flat row-major array: texts.Count rows of <paramref name="dimensions"/> floats.</summary>
    public async Task<float[]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        var result = new float[texts.Count * dimensions];
        var missing = new List<int>();

        for (var i = 0; i < texts.Count; i++)
        {
            var cached = cache?.Get(EmbeddingCache.KeyFor(model, dimensions, texts[i]));
            if (cached is { } vector && vector.Length == dimensions)
            {
                vector.CopyTo(result, i * dimensions);
                CacheHits++;
            }
            else
            {
                missing.Add(i);
            }
        }

        logger.LogInformation("Embedding {Missing} of {Total} chunks ({Cached} from cache)", missing.Count, texts.Count, CacheHits);

        for (var offset = 0; offset < missing.Count; offset += Math.Max(1, batchSize))
        {
            var batch = missing.Skip(offset).Take(Math.Max(1, batchSize)).ToList();
            var embeddings = await GenerateWithRetryAsync(batch.Select(i => texts[i]).ToList(), cancellationToken);
            if (embeddings.Count != batch.Count)
            {
                throw new InvalidOperationException($"Provider returned {embeddings.Count} embeddings for {batch.Count} inputs.");
            }

            var toCache = new List<(string, ReadOnlyMemory<float>)>();
            for (var j = 0; j < batch.Count; j++)
            {
                var vector = embeddings[j].Vector;
                if (vector.Length != dimensions)
                {
                    throw new InvalidOperationException($"Provider returned {vector.Length} dimensions; {dimensions} were requested. Check that the model supports the configured dimensions.");
                }

                vector.Span.CopyTo(result.AsSpan(batch[j] * dimensions, dimensions));
                toCache.Add((EmbeddingCache.KeyFor(model, dimensions, texts[batch[j]]), vector));
            }

            cache?.Put(toCache);
            Embedded += batch.Count;
            logger.LogInformation("Embedded {Done}/{Missing}", Math.Min(offset + batch.Count, missing.Count), missing.Count);
        }

        return result;
    }

    private async Task<GeneratedEmbeddings<Embedding<float>>> GenerateWithRetryAsync(List<string> inputs, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await generator.GenerateAsync(inputs, cancellationToken: cancellationToken);
            }
            catch (Exception ex) when (attempt < 4 && !cancellationToken.IsCancellationRequested && ex is not OperationCanceledException)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                logger.LogWarning("Embedding request failed (attempt {Attempt}): {Message}. Retrying in {Delay}s", attempt, ex.Message, delay.TotalSeconds);
                await Task.Delay(delay, cancellationToken);
            }
        }
    }
}
