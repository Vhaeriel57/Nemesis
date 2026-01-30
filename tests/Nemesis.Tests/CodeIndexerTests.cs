using Nemesis.CodeAnalysis.Indexing;
using Nemesis.Shared.DTOs;
using Xunit;

namespace Nemesis.Tests;

public class CodeIndexerTests
{
    [Fact]
    public void CodeIndexer_InitialState_NotIndexed()
    {
        var indexer = new CodeIndexer();

        Assert.False(indexer.IsIndexed);
        Assert.Null(indexer.CurrentProjectPath);
    }

    [Fact]
    public async Task SearchSymbols_WhenNotIndexed_ReturnsEmpty()
    {
        var indexer = new CodeIndexer();

        var results = await indexer.SearchSymbolsAsync("test");

        Assert.Empty(results);
    }

    [Fact]
    public async Task GetSymbolDefinition_WhenNotIndexed_ReturnsNull()
    {
        var indexer = new CodeIndexer();

        var result = await indexer.GetSymbolDefinitionAsync("TestClass");

        Assert.Null(result);
    }
}
