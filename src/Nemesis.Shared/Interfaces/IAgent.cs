using Nemesis.Shared.DTOs;
using Nemesis.Shared.Models;

namespace Nemesis.Shared.Interfaces;

public interface IAgent
{
    string Name { get; }
    AgentType Type { get; }
    string SystemPrompt { get; }
    List<string> Capabilities { get; }

    Task<AgentResponse> ProcessAsync(
        string userMessage,
        AgentContext context,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<AgentStreamEvent> ProcessStreamAsync(
        string userMessage,
        AgentContext context,
        CancellationToken cancellationToken = default);
}

public class AgentContext
{
    public string ProjectPath { get; set; } = string.Empty;
    public List<ChatMessage> ChatHistory { get; set; } = new();
    public Dictionary<string, string> RelevantFiles { get; set; } = new();
    public List<CodeSymbolInfo> RelevantSymbols { get; set; } = new();
    public string? CurrentTaskId { get; set; }
    public string? ConversationId { get; set; }
    public Dictionary<string, object> Metadata { get; set; } = new();
}

public class AgentResponse
{
    public string Content { get; set; } = string.Empty;
    public List<ToolCall> ToolCalls { get; set; } = new();
    public PatchSet? GeneratedPatches { get; set; }
    public TaskPlan? ProposedPlan { get; set; }
    public List<string> Citations { get; set; } = new();
    public bool RequiresConfirmation { get; set; }
    public string? ConfirmationPrompt { get; set; }
    public Dictionary<string, object>? Metadata { get; set; }
}

public class AgentStreamEvent
{
    public AgentStreamEventType Type { get; set; }
    public string? Content { get; set; }
    public ToolCall? ToolCall { get; set; }
    public string? Error { get; set; }
    public AgentType? AgentType { get; set; }  // Which agent is speaking
    public AgentType? TargetAgent { get; set; } // For delegation events
}

public enum AgentStreamEventType
{
    TextDelta,
    ToolCallStart,
    ToolCallProgress,
    ToolCallComplete,
    Complete,
    Error,
    // New events for multi-agent system
    AgentThinking,     // Agent is processing
    Delegation,        // Manager delegating to another agent
    AgentResponse,     // Sub-agent responding
    Synthesis          // Manager synthesizing responses
}

public class CodeSymbolInfo
{
    public string Name { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public string? Documentation { get; set; }
    public List<string> References { get; set; } = new();
}
