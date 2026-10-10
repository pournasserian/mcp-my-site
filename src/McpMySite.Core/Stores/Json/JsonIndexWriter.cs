using System.Text.Json;
using McpMySite.Core.Model;
using McpMySite.Core.Search;

namespace McpMySite.Core.Stores.Json;

public sealed class JsonIndexWriter : IIndexWriter
{
    public string StoreType => IndexFormat.JsonStoreType;

    public async Task WriteAsync(
        string directory,
        IndexManifest manifest,
        IReadOnlyList<PageDocument> pages,
        IReadOnlyList<ChunkRecord> chunks,
        ReadOnlyMemory<float> vectors,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(directory);

        var compact = new JsonSerializerOptions(IndexManifest.JsonOptions) { WriteIndented = false };
        await using (var stream = File.Create(Path.Combine(directory, IndexFormat.PagesFileName)))
        {
            await JsonSerializer.SerializeAsync(stream, pages, compact, cancellationToken);
        }

        await using (var stream = File.Create(Path.Combine(directory, IndexFormat.ChunksFileName)))
        {
            await JsonSerializer.SerializeAsync(stream, chunks, compact, cancellationToken);
        }

        await File.WriteAllBytesAsync(Path.Combine(directory, IndexFormat.VectorsFileName), VectorMath.ToBytes(vectors.Span), cancellationToken);

        // The manifest goes last so a reader never sees a manifest whose data files are still being written.
        await File.WriteAllTextAsync(Path.Combine(directory, IndexFormat.ManifestFileName), manifest.ToJson(), cancellationToken);
    }
}
