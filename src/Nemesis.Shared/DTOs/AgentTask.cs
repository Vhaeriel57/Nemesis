using Nemesis.Shared.Models;

namespace Nemesis.Shared.DTOs;

public class AgentTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public AgentType AssignedAgent { get; set; }
    public AgentTaskStatus Status { get; set; } = AgentTaskStatus.Pending;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public List<string> Steps { get; set; } = new();
    public int CurrentStep { get; set; }
    public string? Result { get; set; }
    public string? ErrorMessage { get; set; }
    public List<AgentTask> SubTasks { get; set; } = new();
    public string? ParentTaskId { get; set; }
    public PatchSet? GeneratedPatches { get; set; }
}

public class TaskPlan
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Objective { get; set; } = string.Empty;
    public List<PlanStep> Steps { get; set; } = new();
    public string Analysis { get; set; } = string.Empty;
    public List<string> Considerations { get; set; } = new();
    public List<string> Risks { get; set; } = new();
}

public class PlanStep
{
    public int Order { get; set; }
    public string Description { get; set; } = string.Empty;
    public AgentType SuggestedAgent { get; set; }
    public List<string> RequiredFiles { get; set; } = new();
    public bool RequiresWebSearch { get; set; }
    public string? ExpectedOutput { get; set; }
}
