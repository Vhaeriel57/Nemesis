namespace Nemesis.Shared.Interfaces;

public interface ITool
{
    string Name { get; }
    string Description { get; }
    ToolDefinition Definition { get; }

    Task<ToolResult> ExecuteAsync(
        Dictionary<string, object> parameters,
        ToolContext context,
        CancellationToken cancellationToken = default);
}

public class ToolContext
{
    public string ProjectPath { get; set; } = string.Empty;
    public string CachePath { get; set; } = string.Empty;
    public bool NetworkEnabled { get; set; } = true;
    public List<string> AllowedPaths { get; set; } = new();
    public IServiceProvider Services { get; set; } = null!;
}

public class ToolResult
{
    public bool Success { get; set; }
    public string Output { get; set; } = string.Empty;
    public object? Data { get; set; }
    public string? Error { get; set; }
    public List<string> AffectedFiles { get; set; } = new();
}
