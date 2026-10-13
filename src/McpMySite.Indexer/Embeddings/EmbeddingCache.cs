using McpMySite.Core.Search;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;

namespace McpMySite.Indexer.Embeddings;

/// <summary>SQLite cache of chunk embeddings keyed by model, dimensions and text, so unchanged chunks are not re-embedded.</summary>
public sealed class EmbeddingCache : IDisposable
{
    private readonly SqliteConnection _connection;

    public EmbeddingCache(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _connection.Open();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS embeddings (key TEXT PRIMARY KEY, vector BLOB NOT NULL, created TEXT NOT NULL)";
        cmd.ExecuteNonQuery();
    }

    public static string KeyFor(string model, int dimensions, string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{model}|{dimensions}|{text}"));
        return Convert.ToHexStringLower(bytes);
    }

    public float[]? Get(string key)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT vector FROM embeddings WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() is byte[] bytes ? VectorMath.FromBytes(bytes) : null;
    }

    public void Put(IEnumerable<(string Key, ReadOnlyMemory<float> Vector)> entries)
    {
        using var transaction = _connection.BeginTransaction();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO embeddings (key, vector, created) VALUES ($key, $vector, $created)";
        cmd.Parameters.Add("$key", SqliteType.Text);
        cmd.Parameters.Add("$vector", SqliteType.Blob);
        cmd.Parameters.Add("$created", SqliteType.Text);
        var now = DateTimeOffset.UtcNow.ToString("O");
        foreach (var (key, vector) in entries)
        {
            cmd.Parameters["$key"].Value = key;
            cmd.Parameters["$vector"].Value = VectorMath.ToBytes(vector.Span);
            cmd.Parameters["$created"].Value = now;
            cmd.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void Dispose() => _connection.Dispose();
}
