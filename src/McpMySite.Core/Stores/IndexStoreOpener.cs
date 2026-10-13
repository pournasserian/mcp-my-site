using McpMySite.Core.Stores.Json;
using McpMySite.Core.Stores.Sqlite;

namespace McpMySite.Core.Stores;

/// <summary>Opens whichever store a directory holds, based on its manifest.</summary>
public static class IndexStoreOpener
{
    public static async Task<IndexManifest> ReadManifestAsync(string directory, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(directory, IndexFormat.ManifestFileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"No {IndexFormat.ManifestFileName} in '{directory}'.", path);
        }

        return IndexManifest.Parse(await File.ReadAllTextAsync(path, cancellationToken));
    }

    public static async Task<IIndexStore> OpenAsync(string directory, CancellationToken cancellationToken = default)
    {
        var manifest = await ReadManifestAsync(directory, cancellationToken);
        return manifest.StoreType switch
        {
            IndexFormat.JsonStoreType => await JsonIndexStore.OpenAsync(directory, manifest, cancellationToken),
            IndexFormat.SqliteStoreType => await SqliteIndexStore.OpenAsync(directory, manifest, cancellationToken),
            _ => throw new InvalidDataException($"Unknown store type '{manifest.StoreType}'."),
        };
    }

    public static IIndexWriter CreateWriter(string storeType) => storeType switch
    {
        IndexFormat.JsonStoreType => new JsonIndexWriter(),
        IndexFormat.SqliteStoreType => new SqliteIndexWriter(),
        _ => throw new ArgumentException($"Unknown store type '{storeType}'. Use 'json' or 'sqlite'.", nameof(storeType)),
    };

    /// <summary>File names that make up an index of the given store type, manifest first.</summary>
    public static IReadOnlyList<string> FileNames(string storeType) => storeType switch
    {
        IndexFormat.JsonStoreType => [IndexFormat.ManifestFileName, IndexFormat.PagesFileName, IndexFormat.ChunksFileName, IndexFormat.VectorsFileName],
        IndexFormat.SqliteStoreType => [IndexFormat.ManifestFileName, IndexFormat.SqliteFileName],
        _ => throw new ArgumentException($"Unknown store type '{storeType}'.", nameof(storeType)),
    };
}
