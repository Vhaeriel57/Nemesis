using Nemesis.Shared.DTOs;

namespace Nemesis.Shared.Interfaces;

public interface ICodeIndexer
{
    Task<ProjectInfo> IndexProjectAsync(
        string projectPath,
        IProgress<IndexingProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<List<CodeSymbolInfo>> SearchSymbolsAsync(
        string query,
        int maxResults = 20,
        CancellationToken cancellationToken = default);

    Task<CodeSymbolInfo?> GetSymbolDefinitionAsync(
        string symbolName,
        string? filePath = null,
        CancellationToken cancellationToken = default);

    Task<List<CodeSymbolInfo>> GetReferencesAsync(
        string symbolName,
        CancellationToken cancellationToken = default);

    Task<List<string>> GetDependenciesAsync(
        string filePath,
        CancellationToken cancellationToken = default);

    Task<string> GetFileContentAsync(
        string filePath,
        CancellationToken cancellationToken = default);

    Task<List<CodeSymbolInfo>> GetFileSymbolsAsync(
        string filePath,
        CancellationToken cancellationToken = default);

    Task<Dictionary<string, List<string>>> GetTypeGraphAsync(
        CancellationToken cancellationToken = default);

    bool IsIndexed { get; }
    string? CurrentProjectPath { get; }
}
