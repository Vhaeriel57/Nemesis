using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Nemesis.Shared.DTOs;
using Nemesis.Shared.Interfaces;

namespace Nemesis.AgentCore.LLM;

public class OllamaProvider : ILlmProvider, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OllamaProvider> _logger;
    private readonly string _modelName;
    private readonly string _embeddingModel;
    private bool _isAvailable;

    public string Name => "Ollama";
    public bool IsAvailable => _isAvailable;

    public OllamaProvider(
        string baseUrl = "http://localhost:11434",
        string modelName = "deepseek-coder-v2:16b",
        string embeddingModel = "nomic-embed-text",
        ILogger<OllamaProvider>? logger = null)
    {
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/')),
            Timeout = TimeSpan.FromMinutes(5)
        };
        _modelName = modelName;
        _embeddingModel = embeddingModel;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<OllamaProvider>.Instance;
    }

    public async Task<bool> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _httpClient.GetAsync("/api/tags", cancellationToken);
            _isAvailable = response.IsSuccessStatusCode;
            return _isAvailable;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ollama health check failed");
            _isAvailable = false;
            return false;
        }
    }

    public async Task<string> CompleteAsync(
        string prompt,
        string? systemPrompt = null,
        double temperature = 0.7,
        int maxTokens = 4096,
        CancellationToken cancellationToken = default)
    {
        var request = new OllamaGenerateRequest
        {
            Model = _modelName,
            Prompt = prompt,
            System = systemPrompt,
            Stream = false,
            Options = new OllamaOptions
            {
                Temperature = temperature,
                NumPredict = maxTokens
            }
        };

        var response = await _httpClient.PostAsJsonAsync("/api/generate", request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OllamaGenerateResponse>(cancellationToken: cancellationToken);
        return result?.Response ?? string.Empty;
    }

    public async IAsyncEnumerable<string> StreamCompleteAsync(
        string prompt,
        string? systemPrompt = null,
        double temperature = 0.7,
        int maxTokens = 4096,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = new OllamaGenerateRequest
        {
            Model = _modelName,
            Prompt = prompt,
            System = systemPrompt,
            Stream = true,
            Options = new OllamaOptions
            {
                Temperature = temperature,
                NumPredict = maxTokens
            }
        };

        var jsonContent = JsonSerializer.Serialize(request);
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/generate")
        {
            Content = new StringContent(jsonContent, Encoding.UTF8, "application/json")
        };

        var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var line = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrEmpty(line))
                continue;

            var chunk = JsonSerializer.Deserialize<OllamaGenerateResponse>(line);
            if (chunk?.Response != null)
                yield return chunk.Response;

            if (chunk?.Done == true)
                break;
        }
    }

    public async Task<string> CompleteWithToolsAsync(
        List<ChatMessage> messages,
        List<ToolDefinition> tools,
        string? systemPrompt = null,
        double temperature = 0.7,
        int maxTokens = 4096,
        CancellationToken cancellationToken = default)
    {
        // Format messages for chat completion
        var ollamaMessages = messages.Select(m => new OllamaChatMessage
        {
            Role = m.Role,
            Content = m.Content
        }).ToList();

        if (!string.IsNullOrEmpty(systemPrompt))
        {
            ollamaMessages.Insert(0, new OllamaChatMessage
            {
                Role = "system",
                Content = systemPrompt
            });
        }

        // Add tool definitions to system message
        if (tools.Any())
        {
            var toolsDescription = FormatToolsForPrompt(tools);
            var systemMsg = ollamaMessages.FirstOrDefault(m => m.Role == "system");
            if (systemMsg != null)
            {
                systemMsg.Content += "\n\n" + toolsDescription;
            }
            else
            {
                ollamaMessages.Insert(0, new OllamaChatMessage
                {
                    Role = "system",
                    Content = toolsDescription
                });
            }
        }

        var request = new OllamaChatRequest
        {
            Model = _modelName,
            Messages = ollamaMessages,
            Stream = false,
            Options = new OllamaOptions
            {
                Temperature = temperature,
                NumPredict = maxTokens
            }
        };

        var response = await _httpClient.PostAsJsonAsync("/api/chat", request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OllamaChatResponse>(cancellationToken: cancellationToken);
        return result?.Message?.Content ?? string.Empty;
    }

    private string FormatToolsForPrompt(List<ToolDefinition> tools)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You have access to the following tools:");
        sb.AppendLine();

        foreach (var tool in tools)
        {
            sb.AppendLine($"### {tool.Name}");
            sb.AppendLine(tool.Description);
            sb.AppendLine("Parameters:");
            foreach (var (name, param) in tool.Parameters)
            {
                var required = tool.Required.Contains(name) ? " (required)" : " (optional)";
                sb.AppendLine($"  - {name}: {param.Type}{required} - {param.Description}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("To use a tool, respond with a JSON object in this format:");
        sb.AppendLine("```json");
        sb.AppendLine("{\"tool\": \"tool_name\", \"parameters\": {\"param1\": \"value1\"}}");
        sb.AppendLine("```");

        return sb.ToString();
    }

    public async Task<float[]> GetEmbeddingAsync(string text, CancellationToken cancellationToken = default)
    {
        var request = new OllamaEmbeddingRequest
        {
            Model = _embeddingModel,
            Prompt = text
        };

        var response = await _httpClient.PostAsJsonAsync("/api/embeddings", request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OllamaEmbeddingResponse>(cancellationToken: cancellationToken);
        return result?.Embedding ?? Array.Empty<float>();
    }

    public async Task<List<float[]>> GetEmbeddingsAsync(List<string> texts, CancellationToken cancellationToken = default)
    {
        var results = new List<float[]>();

        foreach (var text in texts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var embedding = await GetEmbeddingAsync(text, cancellationToken);
            results.Add(embedding);
        }

        return results;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}

// Ollama API DTOs
internal class OllamaGenerateRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = string.Empty;

    [JsonPropertyName("system")]
    public string? System { get; set; }

    [JsonPropertyName("stream")]
    public bool Stream { get; set; }

    [JsonPropertyName("options")]
    public OllamaOptions? Options { get; set; }
}

internal class OllamaGenerateResponse
{
    [JsonPropertyName("response")]
    public string? Response { get; set; }

    [JsonPropertyName("done")]
    public bool Done { get; set; }
}

internal class OllamaChatRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("messages")]
    public List<OllamaChatMessage> Messages { get; set; } = new();

    [JsonPropertyName("stream")]
    public bool Stream { get; set; }

    [JsonPropertyName("options")]
    public OllamaOptions? Options { get; set; }
}

internal class OllamaChatMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;
}

internal class OllamaChatResponse
{
    [JsonPropertyName("message")]
    public OllamaChatMessage? Message { get; set; }

    [JsonPropertyName("done")]
    public bool Done { get; set; }
}

internal class OllamaOptions
{
    [JsonPropertyName("temperature")]
    public double Temperature { get; set; }

    [JsonPropertyName("num_predict")]
    public int NumPredict { get; set; }
}

internal class OllamaEmbeddingRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = string.Empty;
}

internal class OllamaEmbeddingResponse
{
    [JsonPropertyName("embedding")]
    public float[]? Embedding { get; set; }
}
