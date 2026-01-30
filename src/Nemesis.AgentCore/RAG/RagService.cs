using Microsoft.Extensions.Logging;
using Nemesis.CodeAnalysis.Indexing;
using Nemesis.CodeAnalysis.Models;
using Nemesis.Shared.DTOs;
using Nemesis.Shared.Interfaces;

namespace Nemesis.AgentCore.RAG;

public class RagService
{
    private readonly IVectorStore _vectorStore;
    private readonly ILlmProvider _llmProvider;
    private readonly CodeChunker _chunker;
    private readonly ILogger<RagService> _logger;
    private bool _isIndexed;

    public bool IsIndexed => _isIndexed;

    public RagService(
        IVectorStore vectorStore,
        ILlmProvider llmProvider,
        ILogger<RagService>? logger = null)
    {
        _vectorStore = vectorStore;
        _llmProvider = llmProvider;
        _chunker = new CodeChunker();
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<RagService>.Instance;
    }

    public async Task IndexProjectAsync(
        ProjectIndex projectIndex,
        IProgress<IndexingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var files = projectIndex.Files.Values.ToList();
        var allChunks = new List<CodeChunk>();

        // Phase 1: Create chunks
        progress?.Report(new IndexingProgress
        {
            Phase = "Creating chunks",
            Current = 0,
            Total = files.Count
        });

        for (int i = 0; i < files.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var file = files[i];
            var chunks = _chunker.ChunkFile(file);
            allChunks.AddRange(chunks);

            progress?.Report(new IndexingProgress
            {
                Phase = "Creating chunks",
                Current = i + 1,
                Total = files.Count,
                CurrentFile = file.RelativePath
            });
        }

        _logger.LogInformation("Created {ChunkCount} chunks from {FileCount} files", allChunks.Count, files.Count);

        // Phase 2: Generate embeddings and store
        progress?.Report(new IndexingProgress
        {
            Phase = "Generating embeddings",
            Current = 0,
            Total = allChunks.Count
        });

        var batchSize = 10;
        var documents = new List<VectorDocument>();

        for (int i = 0; i < allChunks.Count; i += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batch = allChunks.Skip(i).Take(batchSize).ToList();
            var texts = batch.Select(c => _chunker.CreateSearchableText(c)).ToList();

            try
            {
                var embeddings = await _llmProvider.GetEmbeddingsAsync(texts, cancellationToken);

                for (int j = 0; j < batch.Count && j < embeddings.Count; j++)
                {
                    documents.Add(new VectorDocument
                    {
                        Id = batch[j].Id,
                        Content = batch[j].Content,
                        Embedding = embeddings[j],
                        Metadata = batch[j].Metadata
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to generate embeddings for batch starting at {Index}", i);
            }

            progress?.Report(new IndexingProgress
            {
                Phase = "Generating embeddings",
                Current = Math.Min(i + batchSize, allChunks.Count),
                Total = allChunks.Count,
                CurrentFile = batch.FirstOrDefault()?.FilePath ?? ""
            });
        }

        // Phase 3: Store in vector database
        progress?.Report(new IndexingProgress
        {
            Phase = "Storing vectors",
            Current = 0,
            Total = 1
        });

        await _vectorStore.AddDocumentsAsync(documents, cancellationToken);
        _isIndexed = true;

        _logger.LogInformation("Indexed {DocumentCount} documents in vector store", documents.Count);
    }

    public async Task<List<RetrievedContext>> RetrieveAsync(
        string query,
        int topK = 10,
        Dictionary<string, string>? filter = null,
        CancellationToken cancellationToken = default)
    {
        var queryEmbedding = await _llmProvider.GetEmbeddingAsync(query, cancellationToken);
        var results = await _vectorStore.SearchAsync(queryEmbedding, topK, filter, cancellationToken);

        return results.Select(r => new RetrievedContext
        {
            Id = r.Id,
            Content = r.Content,
            Score = r.Score,
            FilePath = r.Metadata.TryGetValue("file", out var file) ? file : "",
            SymbolName = r.Id.Contains("::") ? r.Id.Split("::").Last() : null,
            Metadata = r.Metadata
        }).ToList();
    }

    public async Task<string> BuildContextPromptAsync(
        string query,
        int maxTokens = 4000,
        CancellationToken cancellationToken = default)
    {
        var contexts = await RetrieveAsync(query, topK: 15, cancellationToken: cancellationToken);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Relevant Code Context");
        sb.AppendLine();

        var totalLength = 0;
        var approximateTokensPerChar = 0.25; // Rough estimate

        foreach (var ctx in contexts.OrderByDescending(c => c.Score))
        {
            var entry = $"### {ctx.FilePath} ({ctx.SymbolName ?? ""})\n```csharp\n{ctx.Content}\n```\n\n";
            var estimatedTokens = (int)(entry.Length * approximateTokensPerChar);

            if (totalLength + estimatedTokens > maxTokens)
                break;

            sb.Append(entry);
            totalLength += estimatedTokens;
        }

        return sb.ToString();
    }

    public async Task ClearIndexAsync(CancellationToken cancellationToken = default)
    {
        await _vectorStore.ClearAsync(cancellationToken);
        _isIndexed = false;
    }
}

public class RetrievedContext
{
    public string Id { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public float Score { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public string? SymbolName { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new();
}
