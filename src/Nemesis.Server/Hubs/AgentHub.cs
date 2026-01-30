using Microsoft.AspNetCore.SignalR;
using Nemesis.AgentCore.Orchestration;
using Nemesis.AgentCore.RAG;
using Nemesis.AgentCore.Tools;
using Nemesis.CodeAnalysis.Indexing;
using Nemesis.Server.Services;
using Nemesis.Shared.DTOs;
using Nemesis.Shared.Interfaces;
using Nemesis.Shared.Models;
using NemesisLogLevel = Nemesis.Shared.DTOs.LogLevel;

namespace Nemesis.Server.Hubs;

public class AgentHub : Hub
{
    private readonly AgentOrchestrator _orchestrator;
    private readonly CodeIndexer _codeIndexer;
    private readonly RagService _ragService;
    private readonly PatchTool _patchTool;
    private readonly ProjectService _projectService;
    private readonly LogService _logService;
    private readonly ILlmProvider _llmProvider;
    private readonly ILogger<AgentHub> _logger;

    public AgentHub(
        AgentOrchestrator orchestrator,
        CodeIndexer codeIndexer,
        RagService ragService,
        PatchTool patchTool,
        ProjectService projectService,
        LogService logService,
        ILlmProvider llmProvider,
        ILogger<AgentHub> logger)
    {
        _orchestrator = orchestrator;
        _codeIndexer = codeIndexer;
        _ragService = ragService;
        _patchTool = patchTool;
        _projectService = projectService;
        _logService = logService;
        _llmProvider = llmProvider;
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation("Client connected: {ConnectionId}", Context.ConnectionId);
        _logService.AddLog(NemesisLogLevel.Info, "Hub", $"Client connected: {Context.ConnectionId}");

        // Send current state
        await Clients.Caller.SendAsync("ProjectInfoUpdated", _projectService.CurrentProject);
        await Clients.Caller.SendAsync("LlmStatusUpdated", new
        {
            Provider = _llmProvider.Name,
            IsAvailable = _llmProvider.IsAvailable
        });

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("Client disconnected: {ConnectionId}", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    public async Task<ProjectInfo> IndexProject(string projectPath)
    {
        _logger.LogInformation("Indexing project: {Path}", projectPath);
        _logService.AddLog(NemesisLogLevel.Info, "Indexer", $"Starting indexing: {projectPath}");

        var progress = new Progress<IndexingProgress>(async p =>
        {
            await Clients.Caller.SendAsync("IndexingProgress", p);
        });

        try
        {
            // Index with Roslyn
            var projectInfo = await _codeIndexer.IndexProjectAsync(projectPath, progress);
            _projectService.CurrentProject = projectInfo;

            // Index for RAG
            var index = _codeIndexer.GetIndex();
            if (index != null)
            {
                await _ragService.IndexProjectAsync(index, progress);
            }

            _logService.AddLog(NemesisLogLevel.Info, "Indexer", $"Indexing complete: {projectInfo.TotalFiles} files, {projectInfo.TotalTypes} types");

            await Clients.Caller.SendAsync("ProjectInfoUpdated", projectInfo);
            return projectInfo;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Indexing failed");
            _logService.AddLog(NemesisLogLevel.Error, "Indexer", $"Indexing failed: {ex.Message}");
            throw;
        }
    }

    public async Task SendMessage(string message, string agentType, string sessionId)
    {
        if (!Enum.TryParse<AgentType>(agentType, out var agent))
        {
            await Clients.Caller.SendAsync("Error", $"Unknown agent type: {agentType}");
            return;
        }

        _logService.AddLog(NemesisLogLevel.Info, "Chat", $"[{agentType}] User: {message.Substring(0, Math.Min(100, message.Length))}...");

        try
        {
            await foreach (var evt in _orchestrator.StreamMessageAsync(message, agent, sessionId))
            {
                await Clients.Caller.SendAsync("AgentStreamEvent", evt);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Message processing failed");
            _logService.AddLog(NemesisLogLevel.Error, "Chat", $"Error: {ex.Message}");
            await Clients.Caller.SendAsync("Error", ex.Message);
        }
    }

    public async Task<AgentResponse> SendMessageSync(string message, string agentType, string sessionId)
    {
        if (!Enum.TryParse<AgentType>(agentType, out var agent))
        {
            throw new ArgumentException($"Unknown agent type: {agentType}");
        }

        _logService.AddLog(NemesisLogLevel.Info, "Chat", $"[{agentType}] User: {message.Substring(0, Math.Min(100, message.Length))}...");

        try
        {
            var response = await _orchestrator.SendMessageAsync(message, agent, sessionId);
            _logService.AddLog(NemesisLogLevel.Info, "Chat", $"[{agentType}] Response received ({response.Content.Length} chars)");
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Message processing failed");
            _logService.AddLog(NemesisLogLevel.Error, "Chat", $"Error: {ex.Message}");
            throw;
        }
    }

    public List<ChatMessage> GetChatHistory(string sessionId)
    {
        return _orchestrator.GetChatHistory(sessionId);
    }

    public void ClearChatHistory(string sessionId)
    {
        _orchestrator.ClearChatHistory(sessionId);
        _logService.AddLog(NemesisLogLevel.Info, "Chat", $"Chat history cleared for session: {sessionId}");
    }

    public async Task<TaskPlan> CreatePlan(string objective)
    {
        _logService.AddLog(NemesisLogLevel.Info, "Planner", $"Creating plan for: {objective}");

        try
        {
            var plan = await _orchestrator.CreatePlanAsync(objective);
            _logService.AddLog(NemesisLogLevel.Info, "Planner", $"Plan created with {plan.Steps.Count} steps");
            return plan;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Plan creation failed");
            _logService.AddLog(NemesisLogLevel.Error, "Planner", $"Plan creation failed: {ex.Message}");
            throw;
        }
    }

    public IEnumerable<FilePatch> GetPendingPatches()
    {
        return _patchTool.GetAllPendingPatches();
    }

    public async Task<bool> ApplyPatch(string patchId)
    {
        _logService.AddLog(NemesisLogLevel.Info, "Patch", $"Applying patch: {patchId}");

        var patch = _patchTool.GetPendingPatch(patchId);
        if (patch == null)
        {
            _logService.AddLog(NemesisLogLevel.Error, "Patch", $"Patch not found: {patchId}");
            return false;
        }

        var success = await _patchTool.ApplyPatchAsync(patch);

        if (success)
        {
            _patchTool.RemovePendingPatch(patchId);
            _logService.AddLog(NemesisLogLevel.Info, "Patch", $"Patch applied successfully: {patch.FilePath}");
            await Clients.Caller.SendAsync("PatchApplied", patch);
        }
        else
        {
            _logService.AddLog(NemesisLogLevel.Error, "Patch", $"Patch failed: {patch.ErrorMessage}");
        }

        return success;
    }

    public async Task<bool> RollbackPatch(string patchId)
    {
        _logService.AddLog(NemesisLogLevel.Info, "Patch", $"Rolling back patch: {patchId}");

        var patch = _patchTool.GetPendingPatch(patchId);
        if (patch == null)
        {
            return false;
        }

        var success = await _patchTool.RollbackPatchAsync(patch);

        if (success)
        {
            _logService.AddLog(NemesisLogLevel.Info, "Patch", $"Patch rolled back: {patch.FilePath}");
            await Clients.Caller.SendAsync("PatchRolledBack", patch);
        }

        return success;
    }

    public async Task<bool> CheckLlmHealth()
    {
        var isHealthy = await _llmProvider.CheckHealthAsync();

        await Clients.Caller.SendAsync("LlmStatusUpdated", new
        {
            Provider = _llmProvider.Name,
            IsAvailable = isHealthy
        });

        return isHealthy;
    }

    public List<LogEntry> GetRecentLogs(int count = 100)
    {
        return _logService.GetRecentLogs(count);
    }

    public async Task<List<CodeSymbolInfo>> SearchSymbols(string query, int maxResults = 20)
    {
        return await _codeIndexer.SearchSymbolsAsync(query, maxResults);
    }

    public async Task<string> GetFileContent(string filePath)
    {
        return await _codeIndexer.GetFileContentAsync(filePath);
    }
}
