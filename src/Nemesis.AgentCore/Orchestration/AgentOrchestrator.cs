using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Nemesis.AgentCore.Agents;
using Nemesis.AgentCore.RAG;
using Nemesis.CodeAnalysis.Indexing;
using Nemesis.Shared.DTOs;
using Nemesis.Shared.Interfaces;
using Nemesis.Shared.Models;

namespace Nemesis.AgentCore.Orchestration;

public class AgentOrchestrator
{
    private readonly Dictionary<AgentType, IAgent> _agents;
    private readonly CodeIndexer _codeIndexer;
    private readonly RagService _ragService;
    private readonly ILogger<AgentOrchestrator> _logger;

    private readonly Dictionary<string, List<ChatMessage>> _chatHistories = new();
    private readonly Queue<AgentTask> _taskQueue = new();
    private AgentTask? _currentTask;

    public event Action<AgentTask>? TaskStarted;
    public event Action<AgentTask>? TaskCompleted;
    public event Action<AgentTask>? TaskFailed;
#pragma warning disable CS0067 // Event is never used - reserved for future implementation
    public event Action<string, string>? MessageReceived;
#pragma warning restore CS0067

    public AgentOrchestrator(
        IEnumerable<IAgent> agents,
        CodeIndexer codeIndexer,
        RagService ragService,
        ILogger<AgentOrchestrator> logger)
    {
        _agents = agents.ToDictionary(a => a.Type, a => a);
        _codeIndexer = codeIndexer;
        _ragService = ragService;
        _logger = logger;
    }

    public async Task<AgentResponse> SendMessageAsync(
        string message,
        AgentType agentType,
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        sessionId ??= Guid.NewGuid().ToString();

        if (!_agents.TryGetValue(agentType, out var agent))
        {
            throw new ArgumentException($"Unknown agent type: {agentType}");
        }

        // Get or create chat history
        if (!_chatHistories.TryGetValue(sessionId, out var history))
        {
            history = new List<ChatMessage>();
            _chatHistories[sessionId] = history;
        }

        // Build context
        var context = await BuildContextAsync(message, history, cancellationToken);

        // Process message
        _logger.LogInformation("Sending message to {Agent}: {Message}",
            agent.Name, message.Substring(0, Math.Min(100, message.Length)));

        var response = await agent.ProcessAsync(message, context, cancellationToken);

        // Update history
        history.Add(new ChatMessage
        {
            Role = "user",
            Content = message,
            AgentType = agentType.ToString()
        });

        history.Add(new ChatMessage
        {
            Role = "assistant",
            Content = response.Content,
            AgentType = agentType.ToString(),
            ToolCalls = response.ToolCalls
        });

        // Trim history if too long
        while (history.Count > 100)
        {
            history.RemoveAt(0);
        }

        return response;
    }

    public async IAsyncEnumerable<AgentStreamEvent> StreamMessageAsync(
        string message,
        AgentType agentType,
        string? sessionId = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        sessionId ??= Guid.NewGuid().ToString();

        if (!_agents.TryGetValue(agentType, out var agent))
        {
            yield return new AgentStreamEvent
            {
                Type = AgentStreamEventType.Error,
                Error = $"Unknown agent type: {agentType}"
            };
            yield break;
        }

        if (!_chatHistories.TryGetValue(sessionId, out var history))
        {
            history = new List<ChatMessage>();
            _chatHistories[sessionId] = history;
        }

        var context = await BuildContextAsync(message, history, cancellationToken);

        var fullResponse = new System.Text.StringBuilder();
        string? workingMemory = null;

        await foreach (var evt in agent.ProcessStreamAsync(message, context, cancellationToken))
        {
            if (evt.Type == AgentStreamEventType.TextDelta && evt.Content != null)
            {
                fullResponse.Append(evt.Content);
            }
            else if (evt.Type == AgentStreamEventType.WorkingMemory && evt.Content != null)
            {
                workingMemory = evt.Content;
                // Don't yield WorkingMemory events to the UI
                continue;
            }
            yield return evt;
        }

        // Update history
        history.Add(new ChatMessage { Role = "user", Content = message, AgentType = agentType.ToString() });
        history.Add(new ChatMessage { Role = "assistant", Content = fullResponse.ToString(), AgentType = agentType.ToString() });

        // Save working memory so "continue" picks up context
        if (!string.IsNullOrEmpty(workingMemory))
        {
            history.Add(new ChatMessage
            {
                Role = "system",
                Content = workingMemory,
                AgentType = agentType.ToString(),
                Metadata = new Dictionary<string, object> { ["type"] = "working_memory" }
            });
        }
    }

    private async Task<AgentContext> BuildContextAsync(
        string message,
        List<ChatMessage> history,
        CancellationToken cancellationToken)
    {
        var context = new AgentContext
        {
            ProjectPath = _codeIndexer.CurrentProjectPath ?? "",
            ChatHistory = history.TakeLast(20).ToList()
        };

        // Detect if this is a general project question
        var isGeneralProjectQuestion = IsGeneralProjectQuestion(message);

        // Add relevant code context from RAG if indexed
        if (_ragService.IsIndexed)
        {
            try
            {
                // For general questions, use broader search terms
                var searchQueries = isGeneralProjectQuestion
                    ? new[] { "MonoBehaviour", "NetworkBehaviour", "GameManager", "Player", message }
                    : new[] { message };

                var allResults = new List<RetrievedContext>();

                foreach (var query in searchQueries)
                {
                    var relevantContexts = await _ragService.RetrieveAsync(
                        query,
                        topK: isGeneralProjectQuestion ? 3 : 5,
                        cancellationToken: cancellationToken);
                    allResults.AddRange(relevantContexts);
                }

                // Deduplicate by file path
                var seenFiles = new HashSet<string>();
                foreach (var ctx in allResults)
                {
                    if (seenFiles.Contains(ctx.FilePath))
                        continue;

                    seenFiles.Add(ctx.FilePath);
                    context.RelevantFiles[ctx.FilePath] = ctx.Content;

                    if (!string.IsNullOrEmpty(ctx.SymbolName))
                    {
                        context.RelevantSymbols.Add(new CodeSymbolInfo
                        {
                            Name = ctx.SymbolName,
                            FullName = ctx.SymbolName,
                            FilePath = ctx.FilePath,
                            Kind = ctx.Metadata.TryGetValue("kind", out var kind) ? kind : "Unknown"
                        });
                    }

                    // Limit to 10 files max
                    if (seenFiles.Count >= 10)
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to retrieve RAG context");
            }
        }

        // For general project questions, also search for key types
        if (isGeneralProjectQuestion && _codeIndexer.IsIndexed)
        {
            try
            {
                // Search for common Unity patterns
                var keySearches = new[] { "Manager", "Controller", "Player", "Network", "Game" };
                foreach (var searchTerm in keySearches)
                {
                    var symbols = await _codeIndexer.SearchSymbolsAsync(searchTerm, 3, cancellationToken);
                    foreach (var symbol in symbols)
                    {
                        if (!context.RelevantSymbols.Any(s => s.FullName == symbol.FullName))
                        {
                            context.RelevantSymbols.Add(symbol);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to search for key types");
            }
        }

        return context;
    }

    private bool IsGeneralProjectQuestion(string message)
    {
        var lowerMessage = message.ToLowerInvariant();
        var generalPatterns = new[]
        {
            "mon projet", "my project", "le projet", "the project",
            "dis-moi", "tell me", "que peux", "what can",
            "décris", "describe", "présente", "présentation",
            "overview", "résumé", "summary", "analyse",
            "c'est quoi", "what is this", "à propos", "about"
        };

        return generalPatterns.Any(p => lowerMessage.Contains(p));
    }

    public async Task<TaskPlan> CreatePlanAsync(
        string objective,
        AgentType planningAgent = AgentType.SeniorUnityCSharp,
        CancellationToken cancellationToken = default)
    {
        var planPrompt = $@"Create a detailed plan for the following objective:

{objective}

Respond with a structured plan including:
1. Analysis of what needs to be done
2. Ordered list of steps with:
   - Description
   - Which agent should handle it (SeniorUnityCSharp, Generalist, or Researcher)
   - Required files to examine
   - Whether web search is needed
3. Considerations and potential risks

Format as structured data that can be parsed.";

        var response = await SendMessageAsync(planPrompt, planningAgent, cancellationToken: cancellationToken);

        // Parse the response into a TaskPlan
        return ParsePlanFromResponse(objective, response.Content);
    }

    private TaskPlan ParsePlanFromResponse(string objective, string response)
    {
        var plan = new TaskPlan
        {
            Objective = objective,
            Analysis = response
        };

        // Simple parsing - in production would use more structured approach
        var lines = response.Split('\n');
        var currentStep = 0;

        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith("- ") || line.TrimStart().StartsWith("* ") ||
                System.Text.RegularExpressions.Regex.IsMatch(line.Trim(), @"^\d+\."))
            {
                currentStep++;
                plan.Steps.Add(new PlanStep
                {
                    Order = currentStep,
                    Description = line.TrimStart('-', '*', ' ', '\t').Trim(),
                    SuggestedAgent = AgentType.SeniorUnityCSharp // Default
                });
            }
        }

        return plan;
    }

    public void QueueTask(AgentTask task)
    {
        _taskQueue.Enqueue(task);
        _logger.LogInformation("Task queued: {TaskId} - {Title}", task.Id, task.Title);
    }

    public async Task ProcessQueueAsync(CancellationToken cancellationToken = default)
    {
        while (_taskQueue.TryDequeue(out var task))
        {
            cancellationToken.ThrowIfCancellationRequested();

            _currentTask = task;
            task.Status = AgentTaskStatus.InProgress;
            task.StartedAt = DateTime.UtcNow;
            TaskStarted?.Invoke(task);

            try
            {
                var response = await SendMessageAsync(
                    task.Description,
                    task.AssignedAgent,
                    task.Id,
                    cancellationToken);

                task.Result = response.Content;
                task.GeneratedPatches = response.GeneratedPatches;
                task.Status = AgentTaskStatus.Completed;
                task.CompletedAt = DateTime.UtcNow;
                TaskCompleted?.Invoke(task);
            }
            catch (Exception ex)
            {
                task.Status = AgentTaskStatus.Failed;
                task.ErrorMessage = ex.Message;
                task.CompletedAt = DateTime.UtcNow;
                TaskFailed?.Invoke(task);
                _logger.LogError(ex, "Task failed: {TaskId}", task.Id);
            }

            _currentTask = null;
        }
    }

    public List<ChatMessage> GetChatHistory(string sessionId) =>
        _chatHistories.TryGetValue(sessionId, out var history)
            ? history.ToList()
            : new List<ChatMessage>();

    public void ClearChatHistory(string sessionId) =>
        _chatHistories.Remove(sessionId);

    public AgentTask? GetCurrentTask() => _currentTask;
    public IEnumerable<AgentTask> GetQueuedTasks() => _taskQueue.ToArray();
    public IAgent? GetAgent(AgentType type) => _agents.TryGetValue(type, out var agent) ? agent : null;
}
