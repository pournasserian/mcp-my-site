namespace McpMySite.Core.Stores;

/// <summary>Writes a complete index to a directory. Every run is a full rebuild.</summary>
public interface IIndexWriter
{
    string StoreType { get; }

    /// <summary>
    /// <paramref name="vectors"/> is a flat row-major array with one row per chunk, in the same order as
    /// <paramref name="chunks"/>, each row <see cref="IndexManifest.EmbeddingDimensions"/> long.
    /// </summary>
    Task WriteAsync(
        string directory,
        IndexManifest manifest,
        IReadOnlyList<PageDocument> pages,
        IReadOnlyList<ChunkRecord> chunks,
        ReadOnlyMemory<float> vectors,
        CancellationToken cancellationToken = default);
}
