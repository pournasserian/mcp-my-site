using McpMySite.Core.Search;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace McpMySite.Core.Stores.Sqlite;

public sealed class SqliteIndexWriter : IIndexWriter
{
    public string StoreType => IndexFormat.SqliteStoreType;

    public async Task WriteAsync(
        string directory,
        IndexManifest manifest,
        IReadOnlyList<PageDocument> pages,
        IReadOnlyList<ChunkRecord> chunks,
        ReadOnlyMemory<float> vectors,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, IndexFormat.SqliteFileName);
        var temp = path + ".tmp";
        File.Delete(temp);

        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temp, Pooling = false }.ToString()))
        {
            await connection.OpenAsync(cancellationToken);
            await Execute(connection, """
                PRAGMA journal_mode = OFF;
                PRAGMA synchronous = OFF;
                CREATE TABLE manifest (json TEXT NOT NULL);
                CREATE TABLE pages (
                    id INTEGER PRIMARY KEY,
                    url TEXT NOT NULL,
                    url_key TEXT NOT NULL,
                    title TEXT NOT NULL,
                    description TEXT,
                    language TEXT NOT NULL,
                    headings TEXT NOT NULL,
                    modified TEXT,
                    markdown TEXT NOT NULL
                );
                CREATE UNIQUE INDEX ix_pages_url_key ON pages(url_key);
                CREATE TABLE chunks (
                    id INTEGER PRIMARY KEY,
                    page_id INTEGER NOT NULL REFERENCES pages(id),
                    ordinal INTEGER NOT NULL,
                    heading_path TEXT NOT NULL,
                    text TEXT NOT NULL,
                    vector BLOB NOT NULL
                );
                CREATE INDEX ix_chunks_page ON chunks(page_id);
                CREATE VIRTUAL TABLE chunks_fts USING fts5(chunk_id UNINDEXED, content, tokenize = 'unicode61 remove_diacritics 2');
                """, cancellationToken);

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO manifest (json) VALUES ($json)";
                cmd.Parameters.AddWithValue("$json", manifest.ToJson());
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            var titles = new Dictionary<int, string>();
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT INTO pages (id, url, url_key, title, description, language, headings, modified, markdown)
                    VALUES ($id, $url, $key, $title, $description, $language, $headings, $modified, $markdown)
                    """;
                var p = cmd.Parameters;
                p.Add("$id", SqliteType.Integer); p.Add("$url", SqliteType.Text); p.Add("$key", SqliteType.Text);
                p.Add("$title", SqliteType.Text); p.Add("$description", SqliteType.Text); p.Add("$language", SqliteType.Text);
                p.Add("$headings", SqliteType.Text); p.Add("$modified", SqliteType.Text); p.Add("$markdown", SqliteType.Text);
                foreach (var page in pages)
                {
                    titles[page.Id] = page.Title;
                    p["$id"].Value = page.Id;
                    p["$url"].Value = page.Url;
                    p["$key"].Value = UrlKey.Normalize(page.Url);
                    p["$title"].Value = page.Title;
                    p["$description"].Value = (object?)page.Description ?? DBNull.Value;
                    p["$language"].Value = page.Language;
                    p["$headings"].Value = JsonSerializer.Serialize(page.Headings);
                    p["$modified"].Value = page.Modified is { } m ? m.ToString("O") : DBNull.Value;
                    p["$markdown"].Value = page.Markdown;
                    await cmd.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            var dims = manifest.EmbeddingDimensions;
            await using (var cmd = connection.CreateCommand())
            await using (var fts = connection.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO chunks (id, page_id, ordinal, heading_path, text, vector) VALUES ($id, $page, $ordinal, $path, $text, $vector)";
                var p = cmd.Parameters;
                p.Add("$id", SqliteType.Integer); p.Add("$page", SqliteType.Integer); p.Add("$ordinal", SqliteType.Integer);
                p.Add("$path", SqliteType.Text); p.Add("$text", SqliteType.Text); p.Add("$vector", SqliteType.Blob);

                fts.CommandText = "INSERT INTO chunks_fts (chunk_id, content) VALUES ($id, $content)";
                fts.Parameters.Add("$id", SqliteType.Integer);
                fts.Parameters.Add("$content", SqliteType.Text);

                for (var i = 0; i < chunks.Count; i++)
                {
                    var chunk = chunks[i];
                    p["$id"].Value = chunk.Id;
                    p["$page"].Value = chunk.PageId;
                    p["$ordinal"].Value = chunk.Ordinal;
                    p["$path"].Value = JsonSerializer.Serialize(chunk.HeadingPath);
                    p["$text"].Value = chunk.Text;
                    p["$vector"].Value = VectorMath.ToBytes(vectors.Span.Slice(i * dims, dims));
                    await cmd.ExecuteNonQueryAsync(cancellationToken);

                    fts.Parameters["$id"].Value = chunk.Id;
                    fts.Parameters["$content"].Value = ChunkRecord.BuildEmbeddingText(titles[chunk.PageId], chunk.HeadingPath, chunk.Text);
                    await fts.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            await transaction.CommitAsync(cancellationToken);
            await Execute(connection, "INSERT INTO chunks_fts(chunks_fts) VALUES('optimize'); VACUUM;", cancellationToken);
        }

        SqliteConnection.ClearAllPools();
        File.Move(temp, path, overwrite: true);
        await File.WriteAllTextAsync(Path.Combine(directory, IndexFormat.ManifestFileName), manifest.ToJson(), cancellationToken);
    }

    private static async Task Execute(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
