using Nemesis.Shared.DTOs;

namespace Nemesis.Shared.Interfaces;

public interface ILlmProvider
{
    string Name { get; }
    bool IsAvailable { get; }

    Task<string> CompleteAsync(
        string prompt,
        string? systemPrompt = null,
        double temperature = 0.7,
        int maxTokens = 4096,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<string> StreamCompleteAsync(
        string prompt,
        string? systemPrompt = null,
        double temperature = 0.7,
        int maxTokens = 4096,
        CancellationToken cancellationToken = default);

    Task<string> CompleteWithToolsAsync(
        List<ChatMessage> messages,
        List<ToolDefinition> tools,
        string? systemPrompt = null,
        double temperature = 0.7,
        int maxTokens = 4096,
        CancellationToken cancellationToken = default);

    Task<float[]> GetEmbeddingAsync(string text, CancellationToken cancellationToken = default);
    Task<List<float[]>> GetEmbeddingsAsync(List<string> texts, CancellationToken cancellationToken = default);

    Task<bool> CheckHealthAsync(CancellationToken cancellationToken = default);
}

public class ToolDefinition
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public Dictionary<string, ToolParameter> Parameters { get; set; } = new();
    public List<string> Required { get; set; } = new();
}

public class ToolParameter
{
    public string Type { get; set; } = "string";
    public string Description { get; set; } = string.Empty;
    public List<string>? Enum { get; set; }
}
