using Nemesis.AgentCore.RAG;
using Nemesis.Shared.Interfaces;
using Xunit;

namespace Nemesis.Tests;

public class VectorStoreTests : IAsyncLifetime
{
    private readonly SqliteVectorStore _store;
    private readonly string _testDbPath;

    public VectorStoreTests()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"nemesis_test_{Guid.NewGuid()}.db");
        _store = new SqliteVectorStore();
    }

    public async Task InitializeAsync()
    {
        await _store.InitializeAsync(_testDbPath);
    }

    public Task DisposeAsync()
    {
        _store.Dispose();
        if (File.Exists(_testDbPath))
            File.Delete(_testDbPath);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task AddDocument_ThenSearch_ReturnsResult()
    {
        var embedding = new float[] { 1.0f, 0.0f, 0.0f };

        await _store.AddDocumentAsync("doc1", "Test content", embedding);

        var results = await _store.SearchAsync(embedding, topK: 1);

        Assert.Single(results);
        Assert.Equal("doc1", results[0].Id);
        Assert.Equal("Test content", results[0].Content);
    }

    [Fact]
    public async Task GetDocumentCount_AfterAddingDocuments_ReturnsCorrectCount()
    {
        var embedding = new float[] { 1.0f, 0.0f, 0.0f };

        await _store.AddDocumentAsync("doc1", "Content 1", embedding);
        await _store.AddDocumentAsync("doc2", "Content 2", embedding);

        var count = await _store.GetDocumentCountAsync();

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task Clear_RemovesAllDocuments()
    {
        var embedding = new float[] { 1.0f, 0.0f, 0.0f };
        await _store.AddDocumentAsync("doc1", "Content", embedding);

        await _store.ClearAsync();
        var count = await _store.GetDocumentCountAsync();

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task DeleteDocument_RemovesSpecificDocument()
    {
        var embedding = new float[] { 1.0f, 0.0f, 0.0f };
        await _store.AddDocumentAsync("doc1", "Content 1", embedding);
        await _store.AddDocumentAsync("doc2", "Content 2", embedding);

        await _store.DeleteDocumentAsync("doc1");

        var count = await _store.GetDocumentCountAsync();
        Assert.Equal(1, count);

        var results = await _store.SearchAsync(embedding);
        Assert.Single(results);
        Assert.Equal("doc2", results[0].Id);
    }

    [Fact]
    public async Task Search_WithFilter_ReturnsFilteredResults()
    {
        var embedding = new float[] { 1.0f, 0.0f, 0.0f };
        await _store.AddDocumentAsync("doc1", "Content 1", embedding,
            new Dictionary<string, string> { ["type"] = "code" });
        await _store.AddDocumentAsync("doc2", "Content 2", embedding,
            new Dictionary<string, string> { ["type"] = "doc" });

        var results = await _store.SearchAsync(embedding, filter: new Dictionary<string, string> { ["type"] = "code" });

        Assert.Single(results);
        Assert.Equal("doc1", results[0].Id);
    }

    [Fact]
    public async Task CosineSimilarity_SameVector_ReturnsOne()
    {
        var embedding = new float[] { 1.0f, 0.0f, 0.0f };
        await _store.AddDocumentAsync("doc1", "Content", embedding);

        var results = await _store.SearchAsync(embedding, topK: 1);

        Assert.Single(results);
        Assert.Equal(1.0f, results[0].Score, precision: 5);
    }
}
