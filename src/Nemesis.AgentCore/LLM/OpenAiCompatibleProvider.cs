using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Nemesis.Shared.DTOs;
using Nemesis.Shared.Interfaces;

namespace Nemesis.AgentCore.LLM;

public class OpenAiCompatibleProvider : ILlmProvider, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OpenAiCompatibleProvider> _logger;
    private readonly string _modelName;
    private readonly string _embeddingModel;
    private bool _isAvailable;

    public string Name => "OpenAI-Compatible";
    public bool IsAvailable => _isAvailable;

    public OpenAiCompatibleProvider(
        string baseUrl,
        string apiKey,
        string modelName = "gpt-4",
        string embeddingModel = "text-embedding-ada-002",
        ILogger<OpenAiCompatibleProvider>? logger = null)
    {
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/')),
            Timeout = TimeSpan.FromMinutes(5)
        };

        if (!string.IsNullOrEmpty(apiKey))
        {
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
        }

        _modelName = modelName;
        _embeddingModel = embeddingModel;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<OpenAiCompatibleProvider>.Instance;
    }

    public async Task<bool> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _httpClient.GetAsync("/v1/models", cancellationToken);
            _isAvailable = response.IsSuccessStatusCode;
            return _isAvailable;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OpenAI-compatible provider health check failed");
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
        var messages = new List<OpenAiMessage>();

        if (!string.IsNullOrEmpty(systemPrompt))
        {
            messages.Add(new OpenAiMessage { Role = "system", Content = systemPrompt });
        }

        messages.Add(new OpenAiMessage { Role = "user", Content = prompt });

        var request = new OpenAiChatRequest
        {
            Model = _modelName,
            Messages = messages,
            Temperature = temperature,
            MaxTokens = maxTokens,
            Stream = false
        };

        var response = await _httpClient.PostAsJsonAsync("/v1/chat/completions", request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OpenAiChatResponse>(cancellationToken: cancellationToken);
        return result?.Choices?.FirstOrDefault()?.Message?.Content ?? string.Empty;
    }

    public async IAsyncEnumerable<string> StreamCompleteAsync(
        string prompt,
        string? systemPrompt = null,
        double temperature = 0.7,
        int maxTokens = 4096,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var messages = new List<OpenAiMessage>();

        if (!string.IsNullOrEmpty(systemPrompt))
        {
            messages.Add(new OpenAiMessage { Role = "system", Content = systemPrompt });
        }

        messages.Add(new OpenAiMessage { Role = "user", Content = prompt });

        var request = new OpenAiChatRequest
        {
            Model = _modelName,
            Messages = messages,
            Temperature = temperature,
            MaxTokens = maxTokens,
            Stream = true
        };

        var jsonContent = JsonSerializer.Serialize(request);
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
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
            if (string.IsNullOrEmpty(line) || !line.StartsWith("data: "))
                continue;

            var data = line.Substring(6);
            if (data == "[DONE]")
                break;

            var chunk = JsonSerializer.Deserialize<OpenAiStreamResponse>(data);
            var content = chunk?.Choices?.FirstOrDefault()?.Delta?.Content;

            if (!string.IsNullOrEmpty(content))
                yield return content;
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
        var openAiMessages = new List<OpenAiMessage>();

        if (!string.IsNullOrEmpty(systemPrompt))
        {
            openAiMessages.Add(new OpenAiMessage { Role = "system", Content = systemPrompt });
        }

        foreach (var msg in messages)
        {
            openAiMessages.Add(new OpenAiMessage { Role = msg.Role, Content = msg.Content });
        }

        var openAiTools = tools.Select(t => new OpenAiTool
        {
            Type = "function",
            Function = new OpenAiFunction
            {
                Name = t.Name,
                Description = t.Description,
                Parameters = new OpenAiFunctionParameters
                {
                    Type = "object",
                    Properties = t.Parameters.ToDictionary(
                        p => p.Key,
                        p => new OpenAiProperty
                        {
                            Type = p.Value.Type,
                            Description = p.Value.Description,
                            Enum = p.Value.Enum
                        }),
                    Required = t.Required
                }
            }
        }).ToList();

        var request = new OpenAiChatRequest
        {
            Model = _modelName,
            Messages = openAiMessages,
            Temperature = temperature,
            MaxTokens = maxTokens,
            Stream = false,
            Tools = openAiTools
        };

        var response = await _httpClient.PostAsJsonAsync("/v1/chat/completions", request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OpenAiChatResponse>(cancellationToken: cancellationToken);
        return result?.Choices?.FirstOrDefault()?.Message?.Content ?? string.Empty;
    }

    public async Task<float[]> GetEmbeddingAsync(string text, CancellationToken cancellationToken = default)
    {
        var request = new OpenAiEmbeddingRequest
        {
            Model = _embeddingModel,
            Input = text
        };

        var response = await _httpClient.PostAsJsonAsync("/v1/embeddings", request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OpenAiEmbeddingResponse>(cancellationToken: cancellationToken);
        return result?.Data?.FirstOrDefault()?.Embedding ?? Array.Empty<float>();
    }

    public async Task<List<float[]>> GetEmbeddingsAsync(List<string> texts, CancellationToken cancellationToken = default)
    {
        var request = new OpenAiEmbeddingRequest
        {
            Model = _embeddingModel,
            Input = texts
        };

        var response = await _httpClient.PostAsJsonAsync("/v1/embeddings", request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OpenAiEmbeddingResponse>(cancellationToken: cancellationToken);
        return result?.Data?.Select(d => d.Embedding).ToList() ?? new List<float[]>();
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}

// OpenAI API DTOs
internal class OpenAiChatRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("messages")]
    public List<OpenAiMessage> Messages { get; set; } = new();

    [JsonPropertyName("temperature")]
    public double Temperature { get; set; }

    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; set; }

    [JsonPropertyName("stream")]
    public bool Stream { get; set; }

    [JsonPropertyName("tools")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<OpenAiTool>? Tools { get; set; }
}

internal class OpenAiMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;
}

internal class OpenAiChatResponse
{
    [JsonPropertyName("choices")]
    public List<OpenAiChoice>? Choices { get; set; }
}

internal class OpenAiChoice
{
    [JsonPropertyName("message")]
    public OpenAiMessage? Message { get; set; }

    [JsonPropertyName("delta")]
    public OpenAiMessage? Delta { get; set; }
}

internal class OpenAiStreamResponse
{
    [JsonPropertyName("choices")]
    public List<OpenAiChoice>? Choices { get; set; }
}

internal class OpenAiTool
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public OpenAiFunction Function { get; set; } = new();
}

internal class OpenAiFunction
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("parameters")]
    public OpenAiFunctionParameters Parameters { get; set; } = new();
}

internal class OpenAiFunctionParameters
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "object";

    [JsonPropertyName("properties")]
    public Dictionary<string, OpenAiProperty> Properties { get; set; } = new();

    [JsonPropertyName("required")]
    public List<string> Required { get; set; } = new();
}

internal class OpenAiProperty
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "string";

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("enum")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Enum { get; set; }
}

internal class OpenAiEmbeddingRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("input")]
    public object Input { get; set; } = string.Empty;
}

internal class OpenAiEmbeddingResponse
{
    [JsonPropertyName("data")]
    public List<OpenAiEmbeddingData>? Data { get; set; }
}

internal class OpenAiEmbeddingData
{
    [JsonPropertyName("embedding")]
    public float[] Embedding { get; set; } = Array.Empty<float>();
}
