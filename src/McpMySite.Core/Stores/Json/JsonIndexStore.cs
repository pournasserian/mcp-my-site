using McpMySite.Core.Search;
using System.Text.Json;

namespace McpMySite.Core.Stores.Json;

/// <summary>
/// Index held entirely in memory: pages and chunks from JSON files, vectors from a binary sidecar.
/// Best for sites under a few hundred pages.
/// </summary>
public sealed class JsonIndexStore : IIndexStore
{
    private readonly Dictionary<int, PageDocument> _pagesById;
    private readonly Dictionary<string, PageDocument> _pagesByUrl;
    private readonly ChunkRecord[] _chunks;
    private readonly Dictionary<int, int> _chunkRowById;
    private readonly float[] _vectors;
    private readonly Bm25Index _keyword;

    private JsonIndexStore(IndexManifest manifest, List<PageDocument> pages, ChunkRecord[] chunks, float[] vectors)
    {
        Manifest = manifest;
        _pagesById = pages.ToDictionary(p => p.Id);
        _pagesByUrl = new Dictionary<string, PageDocument>(StringComparer.Ordinal);
        foreach (var page in pages)
        {
            _pagesByUrl.TryAdd(UrlKey.Normalize(page.Url), page);
        }

        _chunks = chunks;
        _chunkRowById = new Dictionary<int, int>(chunks.Length);
        for (var i = 0; i < chunks.Length; i++)
        {
            _chunkRowById[chunks[i].Id] = i;
        }

        _vectors = vectors;
        _keyword = new Bm25Index(chunks.Select(c => ChunkRecord.BuildEmbeddingText(_pagesById[c.PageId].Title, c.HeadingPath, c.Text)).ToList());
    }

    public IndexManifest Manifest { get; }

    public static async Task<JsonIndexStore> OpenAsync(string directory, IndexManifest manifest, CancellationToken cancellationToken = default)
    {
        var pages = await ReadJsonAsync<List<PageDocument>>(Path.Combine(directory, IndexFormat.PagesFileName), cancellationToken);
        var chunks = await ReadJsonAsync<ChunkRecord[]>(Path.Combine(directory, IndexFormat.ChunksFileName), cancellationToken);
        var bytes = await File.ReadAllBytesAsync(Path.Combine(directory, IndexFormat.VectorsFileName), cancellationToken);
        var vectors = VectorMath.FromBytes(bytes);

        var expected = (long)chunks.Length * manifest.EmbeddingDimensions;
        if (vectors.Length != expected)
        {
            throw new InvalidDataException(
                $"{IndexFormat.VectorsFileName} holds {vectors.Length} floats but {chunks.Length} chunks x {manifest.EmbeddingDimensions} dimensions = {expected} were expected.");
        }

        if (pages.Count != manifest.PageCount)
        {
            throw new InvalidDataException($"Manifest reports {manifest.PageCount} pages but {IndexFormat.PagesFileName} holds {pages.Count}.");
        }

        for (var row = 0; row < chunks.Length; row++)
        {
            VectorMath.NormalizeInPlace(vectors.AsSpan(row * manifest.EmbeddingDimensions, manifest.EmbeddingDimensions));
        }

        return new JsonIndexStore(manifest, pages, chunks, vectors);
    }

    public Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default)
    {
        var keyword = _keyword.Search(request.Query, request.CandidateCount).Select(r => _chunks[r.DocIndex].Id).ToList();

        var vector = new List<int>();
        if (request.QueryVector is { } q)
        {
            if (q.Length != Manifest.EmbeddingDimensions)
            {
                throw new ArgumentException($"Query vector has {q.Length} dimensions; the index uses {Manifest.EmbeddingDimensions}.");
            }

            vector = VectorMath.TopK(_vectors, Manifest.EmbeddingDimensions, q.Span, request.CandidateCount)
                .Select(r => _chunks[r.Row].Id)
                .ToList();
        }

        var fused = HybridRanker.Fuse(keyword, vector);
        var hits = HybridRanker.ToPageHits(
            fused,
            id => { var c = _chunks[_chunkRowById[id]]; return new HybridRanker.ChunkCandidate(c.Id, c.PageId, c.Ordinal, c.HeadingPath, c.Text); },
            id => _pagesById[id],
            request.Query,
            request.Limit);

        return Task.FromResult(new SearchResponse { Hits = hits, KeywordOnly = request.QueryVector is null });
    }

    public Task<PageDocument?> GetPageAsync(string url, CancellationToken cancellationToken = default)
    {
        _pagesByUrl.TryGetValue(UrlKey.Normalize(url), out var page);
        return Task.FromResult(page);
    }

    public IReadOnlyList<(string Url, string Title)> ListPages() =>
        _pagesById.Values.OrderBy(p => p.Id).Select(p => (p.Url, p.Title)).ToList();

    public void Dispose()
    {
    }

    private static async Task<T> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, IndexManifest.JsonOptions, cancellationToken)
            ?? throw new InvalidDataException($"'{path}' is empty.");
    }
}
