namespace Nemesis.Shared.Interfaces;

public interface IVectorStore
{
    Task InitializeAsync(string storagePath, CancellationToken cancellationToken = default);

    Task AddDocumentAsync(
        string id,
        string content,
        float[] embedding,
        Dictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default);

    Task AddDocumentsAsync(
        List<VectorDocument> documents,
        CancellationToken cancellationToken = default);

    Task<List<VectorSearchResult>> SearchAsync(
        float[] queryEmbedding,
        int topK = 10,
        Dictionary<string, string>? filter = null,
        CancellationToken cancellationToken = default);

    Task DeleteDocumentAsync(string id, CancellationToken cancellationToken = default);
    Task DeleteByMetadataAsync(string key, string value, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);

    Task<int> GetDocumentCountAsync(CancellationToken cancellationToken = default);
}

public class VectorDocument
{
    public string Id { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public float[] Embedding { get; set; } = Array.Empty<float>();
    public Dictionary<string, string> Metadata { get; set; } = new();
}

public class VectorSearchResult
{
    public string Id { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public float Score { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new();
}
