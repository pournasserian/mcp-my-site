using System.Globalization;
using System.Text.Json;
using McpMySite.Core.Model;
using McpMySite.Core.Search;
using Microsoft.Data.Sqlite;

namespace McpMySite.Core.Stores.Sqlite;

/// <summary>
/// SQLite-backed index. Keyword search runs in FTS5; vectors are read into memory once because
/// stock SQLite has no vector index and sites this size fit comfortably.
/// </summary>
public sealed class SqliteIndexStore : IIndexStore
{
    private readonly SqliteConnection _connection;
    private readonly int[] _chunkIdsByRow;
    private readonly float[] _vectors;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private SqliteIndexStore(IndexManifest manifest, SqliteConnection connection, int[] chunkIdsByRow, float[] vectors)
    {
        Manifest = manifest;
        _connection = connection;
        _chunkIdsByRow = chunkIdsByRow;
        _vectors = vectors;
    }

    public IndexManifest Manifest { get; }

    public static async Task<SqliteIndexStore> OpenAsync(string directory, IndexManifest manifest, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(directory, IndexFormat.SqliteFileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"No {IndexFormat.SqliteFileName} in {directory}.", path);
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync(cancellationToken);

        try
        {
            var dims = manifest.EmbeddingDimensions;
            var ids = new List<int>();
            var floats = new List<float>();

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, vector FROM chunks ORDER BY id";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var vector = VectorMath.FromBytes((byte[])reader["vector"]);
                if (vector.Length != dims)
                {
                    throw new InvalidDataException($"Chunk {reader.GetInt32(0)} has {vector.Length} dimensions; manifest says {dims}.");
                }

                VectorMath.NormalizeInPlace(vector);
                ids.Add(reader.GetInt32(0));
                floats.AddRange(vector);
            }

            if (ids.Count != manifest.ChunkCount)
            {
                throw new InvalidDataException($"Manifest reports {manifest.ChunkCount} chunks but the database holds {ids.Count}.");
            }

            return new SqliteIndexStore(manifest, connection, ids.ToArray(), floats.ToArray());
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var keyword = await KeywordSearchAsync(request.Query, request.CandidateCount, cancellationToken);

            var vector = new List<int>();
            if (request.QueryVector is { } q)
            {
                if (q.Length != Manifest.EmbeddingDimensions)
                {
                    throw new ArgumentException($"Query vector has {q.Length} dimensions; the index uses {Manifest.EmbeddingDimensions}.");
                }

                vector = VectorMath.TopK(_vectors, Manifest.EmbeddingDimensions, q.Span, request.CandidateCount)
                    .Select(r => _chunkIdsByRow[r.Row])
                    .ToList();
            }

            var fused = HybridRanker.Fuse(keyword, vector);
            var chunks = await LoadChunksAsync(fused.Select(f => f.ChunkId).ToList(), cancellationToken);
            var pages = await LoadPagesAsync(chunks.Values.Select(c => c.PageId).Distinct().ToList(), cancellationToken);

            var hits = HybridRanker.ToPageHits(fused, id => chunks[id], id => pages[id], request.Query, request.Limit);
            return new SearchResponse { Hits = hits, KeywordOnly = request.QueryVector is null };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PageDocument?> GetPageAsync(string url, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = "SELECT id, url, title, description, language, headings, modified, markdown FROM pages WHERE url_key = $key LIMIT 1";
            command.Parameters.AddWithValue("$key", UrlKey.Normalize(url));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? ReadPage(reader) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public IReadOnlyList<(string Url, string Title)> ListPages()
    {
        _gate.Wait();
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT url, title FROM pages ORDER BY id";
            using var reader = command.ExecuteReader();
            var list = new List<(string, string)>();
            while (reader.Read())
            {
                list.Add((reader.GetString(0), reader.GetString(1)));
            }

            return list;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _connection.Dispose();
        _gate.Dispose();
    }

    private async Task<List<int>> KeywordSearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        var terms = Tokenizer.Tokenize(query).Distinct().ToList();
        if (terms.Count == 0)
        {
            return [];
        }

        // Quote each token so user input can never be parsed as FTS5 syntax.
        var match = string.Join(" OR ", terms.Select(t => "\"" + t.Replace("\"", "\"\"") + "\""));

        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT chunk_id FROM chunks_fts WHERE chunks_fts MATCH $match ORDER BY bm25(chunks_fts) LIMIT $limit";
        command.Parameters.AddWithValue("$match", match);
        command.Parameters.AddWithValue("$limit", limit);

        var ids = new List<int>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(reader.GetInt32(0));
        }

        return ids;
    }

    private async Task<Dictionary<int, HybridRanker.ChunkCandidate>> LoadChunksAsync(List<int> ids, CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, HybridRanker.ChunkCandidate>();
        if (ids.Count == 0)
        {
            return result;
        }

        await using var command = _connection.CreateCommand();
        command.CommandText = $"SELECT id, page_id, ordinal, heading_path, text FROM chunks WHERE id IN ({ParameterList(command, ids)})";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetInt32(0);
            result[id] = new HybridRanker.ChunkCandidate(
                id,
                reader.GetInt32(1),
                reader.GetInt32(2),
                JsonSerializer.Deserialize<string[]>(reader.GetString(3)) ?? [],
                reader.GetString(4));
        }

        return result;
    }

    private async Task<Dictionary<int, PageDocument>> LoadPagesAsync(List<int> ids, CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, PageDocument>();
        if (ids.Count == 0)
        {
            return result;
        }

        await using var command = _connection.CreateCommand();
        command.CommandText = $"SELECT id, url, title, description, language, headings, modified, markdown FROM pages WHERE id IN ({ParameterList(command, ids)})";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var page = ReadPage(reader);
            result[page.Id] = page;
        }

        return result;
    }

    private static string ParameterList(SqliteCommand command, List<int> ids)
    {
        var names = new List<string>(ids.Count);
        for (var i = 0; i < ids.Count; i++)
        {
            var name = "$p" + i;
            names.Add(name);
            command.Parameters.AddWithValue(name, ids[i]);
        }

        return string.Join(",", names);
    }

    private static PageDocument ReadPage(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt32(0),
        Url = reader.GetString(1),
        Title = reader.GetString(2),
        Description = reader.IsDBNull(3) ? null : reader.GetString(3),
        Language = reader.GetString(4),
        Headings = JsonSerializer.Deserialize<string[]>(reader.GetString(5)) ?? [],
        Modified = reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6), null, DateTimeStyles.RoundtripKind),
        Markdown = reader.GetString(7),
    };
}
