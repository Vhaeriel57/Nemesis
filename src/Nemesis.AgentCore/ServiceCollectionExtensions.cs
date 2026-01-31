using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nemesis.AgentCore.Agents;
using Nemesis.AgentCore.LLM;
using Nemesis.AgentCore.Models;
using Nemesis.AgentCore.Orchestration;
using Nemesis.AgentCore.RAG;
using Nemesis.AgentCore.Services;
using Nemesis.AgentCore.Tools;
using Nemesis.CodeAnalysis.Indexing;
using Nemesis.Shared.DTOs;
using Nemesis.Shared.Interfaces;

namespace Nemesis.AgentCore;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddNemesisAgentCore(
        this IServiceCollection services,
        NemesisConfiguration config)
    {
        // Settings
        services.AddSingleton(config);
        services.AddSingleton(config.Llm);
        services.AddSingleton(config.WebSearch);
        services.AddSingleton(config.Project);
        services.AddSingleton(config.Ui);
        services.AddSingleton(config.Storage);

        // LLM Provider
        services.AddSingleton<LlmProviderFactory>();
        services.AddSingleton<ILlmProvider>(sp =>
        {
            var factory = sp.GetRequiredService<LlmProviderFactory>();
            return factory.CreateProvider(config.Llm);
        });

        // Code Analysis
        services.AddSingleton<CodeIndexer>(sp =>
            new CodeIndexer(config.Project.ExcludedFolders, config.Project.ExcludedExtensions));

        // Vector Store & RAG
        services.AddSingleton<IVectorStore, SqliteVectorStore>();
        services.AddSingleton<RagService>();

        // Tools
        services.AddSingleton<FileSystemTool>();
        services.AddSingleton<GitTool>();
        services.AddSingleton<WebSearchTool>(sp => new WebSearchTool(config.WebSearch));
        services.AddSingleton<PatchTool>(sp => new PatchTool(config.Storage.BackupPath, config.Project.AutoBackup));
        services.AddSingleton<CodeIndexTool>(sp => new CodeIndexTool(sp.GetRequiredService<CodeIndexer>()));

        // Tool collection
        services.AddSingleton<IEnumerable<ITool>>(sp => new ITool[]
        {
            sp.GetRequiredService<FileSystemTool>(),
            sp.GetRequiredService<GitTool>(),
            sp.GetRequiredService<WebSearchTool>(),
            sp.GetRequiredService<PatchTool>(),
            sp.GetRequiredService<CodeIndexTool>()
        });

        // Nemesis - Agent unifié all-in-one avec injection complète
        services.AddSingleton<NemesisAgent>(sp =>
        {
            var llmProvider = sp.GetRequiredService<ILlmProvider>();
            var tools = sp.GetRequiredService<IEnumerable<ITool>>();
            var logger = sp.GetRequiredService<ILogger<NemesisAgent>>();
            var patchService = sp.GetRequiredService<PatchTool>() as IPatchService;
            var projectKnowledge = sp.GetRequiredService<ProjectKnowledgeService>();
            return new NemesisAgent(llmProvider, tools, logger, patchService, projectKnowledge);
        });

        // Collection d'agents (contient uniquement Nemesis)
        services.AddSingleton<IEnumerable<IAgent>>(sp => new IAgent[]
        {
            sp.GetRequiredService<NemesisAgent>()
        });

        // Conversation persistence service
        services.AddSingleton<ConversationService>(sp =>
        {
            var conversationsPath = Path.Combine(config.Storage.CachePath, "conversations");
            var logger = sp.GetRequiredService<ILogger<ConversationService>>();
            return new ConversationService(conversationsPath, logger);
        });

        // Project cache service
        services.AddSingleton<ProjectCacheService>();

        // Project Knowledge Service - connaissance complète du projet
        services.AddSingleton<ProjectKnowledgeService>();

        // Orchestrator
        services.AddSingleton<AgentOrchestrator>();

        // Services
        services.AddSingleton<IWebSearchService>(sp => sp.GetRequiredService<WebSearchTool>());
        services.AddSingleton<IPatchService>(sp => sp.GetRequiredService<PatchTool>());
        services.AddSingleton<ICodeIndexer>(sp => sp.GetRequiredService<CodeIndexer>());

        return services;
    }

    public static async Task InitializeNemesisAsync(this IServiceProvider services)
    {
        var config = services.GetRequiredService<NemesisConfiguration>();
        var logger = services.GetRequiredService<ILogger<NemesisConfiguration>>();

        // Create directories
        Directory.CreateDirectory(Path.GetDirectoryName(config.Storage.DatabasePath) ?? "./data");
        Directory.CreateDirectory(Path.GetDirectoryName(config.Storage.VectorStorePath) ?? "./data");
        Directory.CreateDirectory(config.Storage.CachePath);
        Directory.CreateDirectory(config.Storage.BackupPath);
        Directory.CreateDirectory(Path.Combine(config.Storage.CachePath, "conversations"));

        // Initialize vector store
        var vectorStore = services.GetRequiredService<IVectorStore>();
        await vectorStore.InitializeAsync(config.Storage.VectorStorePath);

        // Check LLM health
        var llmProvider = services.GetRequiredService<ILlmProvider>();
        var isHealthy = await llmProvider.CheckHealthAsync();

        if (isHealthy)
        {
            logger.LogInformation("LLM provider {Name} is available", llmProvider.Name);
        }
        else
        {
            logger.LogWarning("LLM provider {Name} is not available. Make sure Ollama is running.", llmProvider.Name);
        }

        logger.LogInformation("Nemesis initialized - Agent unifié prêt");
    }
}
