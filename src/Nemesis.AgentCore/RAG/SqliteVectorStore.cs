using System.Text.Json;
using Microsoft.Data.Sqlite;
using Nemesis.Shared.Interfaces;

namespace Nemesis.AgentCore.RAG;

public class SqliteVectorStore : IVectorStore, IDisposable
{
    private SqliteConnection? _connection;
    private readonly object _lock = new();
    private bool _isInitialized;

    public async Task InitializeAsync(string storagePath, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_isInitialized)
                return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(storagePath) ?? storagePath);

        var connectionString = $"Data Source={storagePath}";
        _connection = new SqliteConnection(connectionString);
        await _connection.OpenAsync(cancellationToken);

        var createTableSql = @"
            CREATE TABLE IF NOT EXISTS vectors (
                id TEXT PRIMARY KEY,
                content TEXT NOT NULL,
                embedding BLOB NOT NULL,
                metadata TEXT,
                created_at DATETIME DEFAULT CURRENT_TIMESTAMP
            );
            CREATE INDEX IF NOT EXISTS idx_vectors_id ON vectors(id);
        ";

        using var cmd = new SqliteCommand(createTableSql, _connection);
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        lock (_lock)
        {
            _isInitialized = true;
        }
    }

    public async Task AddDocumentAsync(
        string id,
        string content,
        float[] embedding,
        Dictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();

        var sql = @"
            INSERT OR REPLACE INTO vectors (id, content, embedding, metadata)
            VALUES (@id, @content, @embedding, @metadata)
        ";

        using var cmd = new SqliteCommand(sql, _connection);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@content", content);
        cmd.Parameters.AddWithValue("@embedding", SerializeEmbedding(embedding));
        cmd.Parameters.AddWithValue("@metadata", metadata != null ? JsonSerializer.Serialize(metadata) : DBNull.Value);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AddDocumentsAsync(
        List<VectorDocument> documents,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();

        using var transaction = _connection!.BeginTransaction();

        try
        {
            var sql = @"
                INSERT OR REPLACE INTO vectors (id, content, embedding, metadata)
                VALUES (@id, @content, @embedding, @metadata)
            ";

            foreach (var doc in documents)
            {
                cancellationToken.ThrowIfCancellationRequested();

                using var cmd = new SqliteCommand(sql, _connection);
                cmd.Transaction = transaction;
                cmd.Parameters.AddWithValue("@id", doc.Id);
                cmd.Parameters.AddWithValue("@content", doc.Content);
                cmd.Parameters.AddWithValue("@embedding", SerializeEmbedding(doc.Embedding));
                cmd.Parameters.AddWithValue("@metadata", doc.Metadata.Any()
                    ? JsonSerializer.Serialize(doc.Metadata)
                    : DBNull.Value);

                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public async Task<List<VectorSearchResult>> SearchAsync(
        float[] queryEmbedding,
        int topK = 10,
        Dictionary<string, string>? filter = null,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();

        var sql = "SELECT id, content, embedding, metadata FROM vectors";
        using var cmd = new SqliteCommand(sql, _connection);

        var results = new List<(string Id, string Content, float Score, Dictionary<string, string> Metadata)>();

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetString(0);
            var content = reader.GetString(1);
            var embeddingBytes = (byte[])reader.GetValue(2);
            var embedding = DeserializeEmbedding(embeddingBytes);
            var metadataJson = reader.IsDBNull(3) ? null : reader.GetString(3);
            var metadata = metadataJson != null
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(metadataJson) ?? new()
                : new Dictionary<string, string>();

            // Apply filter if present
            if (filter != null && filter.Any())
            {
                var matches = filter.All(f =>
                    metadata.TryGetValue(f.Key, out var value) && value == f.Value);
                if (!matches)
                    continue;
            }

            var score = CosineSimilarity(queryEmbedding, embedding);
            results.Add((id, content, score, metadata));
        }

        return results
            .OrderByDescending(r => r.Score)
            .Take(topK)
            .Select(r => new VectorSearchResult
            {
                Id = r.Id,
                Content = r.Content,
                Score = r.Score,
                Metadata = r.Metadata
            })
            .ToList();
    }

    public async Task DeleteDocumentAsync(string id, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();

        var sql = "DELETE FROM vectors WHERE id = @id";
        using var cmd = new SqliteCommand(sql, _connection);
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteByMetadataAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();

        // SQLite JSON functions
        var sql = "DELETE FROM vectors WHERE json_extract(metadata, @key) = @value";
        using var cmd = new SqliteCommand(sql, _connection);
        cmd.Parameters.AddWithValue("@key", $"$.{key}");
        cmd.Parameters.AddWithValue("@value", value);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        EnsureInitialized();

        var sql = "DELETE FROM vectors";
        using var cmd = new SqliteCommand(sql, _connection);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> GetDocumentCountAsync(CancellationToken cancellationToken = default)
    {
        EnsureInitialized();

        var sql = "SELECT COUNT(*) FROM vectors";
        using var cmd = new SqliteCommand(sql, _connection);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result);
    }

    private void EnsureInitialized()
    {
        lock (_lock)
        {
            if (!_isInitialized || _connection == null)
                throw new InvalidOperationException("Vector store not initialized. Call InitializeAsync first.");
        }
    }

    private static byte[] SerializeEmbedding(float[] embedding)
    {
        var bytes = new byte[embedding.Length * sizeof(float)];
        Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] DeserializeEmbedding(byte[] bytes)
    {
        var embedding = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, embedding, 0, bytes.Length);
        return embedding;
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0)
            return 0;

        float dotProduct = 0;
        float magnitudeA = 0;
        float magnitudeB = 0;

        for (int i = 0; i < a.Length; i++)
        {
            dotProduct += a[i] * b[i];
            magnitudeA += a[i] * a[i];
            magnitudeB += b[i] * b[i];
        }

        magnitudeA = MathF.Sqrt(magnitudeA);
        magnitudeB = MathF.Sqrt(magnitudeB);

        if (magnitudeA == 0 || magnitudeB == 0)
            return 0;

        return dotProduct / (magnitudeA * magnitudeB);
    }

    public void Dispose()
    {
        _connection?.Dispose();
    }
}
