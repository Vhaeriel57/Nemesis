using Nemesis.Shared.Models;

namespace Nemesis.Shared.DTOs;

/// <summary>
/// Represents a conversation session with multiple agents
/// </summary>
public class AgentConversation
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Title { get; set; } = "New Conversation";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string? ProjectPath { get; set; }
    public List<AgentMessage> Messages { get; set; } = new();
    public ConversationStatus Status { get; set; } = ConversationStatus.Active;
}

/// <summary>
/// A message in the multi-agent conversation
/// </summary>
public class AgentMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public MessageSender Sender { get; set; } = new();
    public string Content { get; set; } = string.Empty;
    public AgentType? TargetAgent { get; set; }  // If this is a delegation message
    public List<ToolCall>? ToolCalls { get; set; }
    public MessageType Type { get; set; } = MessageType.Chat;
    public Dictionary<string, object>? Metadata { get; set; }
}

/// <summary>
/// Identifies who sent the message
/// </summary>
public class MessageSender
{
    public SenderType Type { get; set; } = SenderType.User;
    public AgentType? AgentType { get; set; }
    public string DisplayName => Type == SenderType.User ? "You" : AgentType?.ToString() ?? "System";
}

public enum SenderType
{
    User,
    Agent,
    System
}

public enum MessageType
{
    Chat,           // Normal chat message
    Delegation,     // Manager delegating to another agent
    AgentThinking,  // Agent internal reasoning
    ToolExecution,  // Tool being executed
    AgentResponse,  // Agent responding to delegation
    Summary         // Manager summarizing agent responses
}

public enum ConversationStatus
{
    Active,
    Completed,
    Archived
}

/// <summary>
/// Session persistence model
/// </summary>
public class ConversationSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastAccessedAt { get; set; } = DateTime.UtcNow;
    public string? ProjectPath { get; set; }
    public int MessageCount { get; set; }
    public string? Summary { get; set; }
}
