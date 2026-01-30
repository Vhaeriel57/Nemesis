using System.Text.Json.Serialization;

namespace Nemesis.Shared.DTOs;

public class ChatMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Role { get; set; } = "user";
    public string Content { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string? AgentType { get; set; }
    public List<ToolCall>? ToolCalls { get; set; }
    public Dictionary<string, object>? Metadata { get; set; }
}

public class ToolCall
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public string? Result { get; set; }
    public bool IsComplete { get; set; }
}
