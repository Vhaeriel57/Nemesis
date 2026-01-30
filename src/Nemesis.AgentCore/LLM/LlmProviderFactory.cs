using Microsoft.Extensions.Logging;
using Nemesis.Shared.DTOs;
using Nemesis.Shared.Interfaces;

namespace Nemesis.AgentCore.LLM;

public class LlmProviderFactory
{
    private readonly ILoggerFactory _loggerFactory;

    public LlmProviderFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
    }

    public ILlmProvider CreateProvider(LlmSettings settings)
    {
        return settings.Provider.ToLowerInvariant() switch
        {
            "ollama" => new OllamaProvider(
                settings.OllamaBaseUrl,
                settings.ModelName,
                settings.EmbeddingModel,
                _loggerFactory.CreateLogger<OllamaProvider>()),

            "openai" or "openai-compatible" => new OpenAiCompatibleProvider(
                settings.OpenAiBaseUrl ?? "https://api.openai.com",
                settings.OpenAiApiKey ?? "",
                settings.ModelName,
                settings.EmbeddingModel,
                _loggerFactory.CreateLogger<OpenAiCompatibleProvider>()),

            _ => throw new ArgumentException($"Unknown LLM provider: {settings.Provider}")
        };
    }

    public async Task<ILlmProvider> CreateProviderWithFallbackAsync(
        LlmSettings settings,
        CancellationToken cancellationToken = default)
    {
        var primary = CreateProvider(settings);

        if (await primary.CheckHealthAsync(cancellationToken))
            return primary;

        if (settings.UseOpenAiFallback &&
            !string.IsNullOrEmpty(settings.OpenAiBaseUrl) &&
            !string.IsNullOrEmpty(settings.OpenAiApiKey))
        {
            var fallbackSettings = new LlmSettings
            {
                Provider = "openai-compatible",
                OpenAiBaseUrl = settings.OpenAiBaseUrl,
                OpenAiApiKey = settings.OpenAiApiKey,
                ModelName = settings.ModelName,
                EmbeddingModel = settings.EmbeddingModel
            };

            var fallback = CreateProvider(fallbackSettings);
            if (await fallback.CheckHealthAsync(cancellationToken))
                return fallback;
        }

        // Return primary even if unavailable - let it fail at call time
        return primary;
    }
}
