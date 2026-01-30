using System.Diagnostics;
using Nemesis.Shared.Interfaces;

namespace Nemesis.AgentCore.Tools;

public class GitTool : ITool
{
    public string Name => "git";
    public string Description => "Interact with Git repository: status, diff, log, branch operations. Read-only by default.";

    public ToolDefinition Definition => new()
    {
        Name = Name,
        Description = Description,
        Parameters = new Dictionary<string, ToolParameter>
        {
            ["action"] = new()
            {
                Type = "string",
                Description = "Git action to perform",
                Enum = new List<string> { "status", "diff", "log", "branch", "show", "blame", "create_branch", "commit" }
            },
            ["target"] = new()
            {
                Type = "string",
                Description = "Target for the action (file path, branch name, commit hash)"
            },
            ["message"] = new()
            {
                Type = "string",
                Description = "Commit message (for commit action)"
            },
            ["count"] = new()
            {
                Type = "integer",
                Description = "Number of items to return (for log action, default: 10)"
            }
        },
        Required = new List<string> { "action" }
    };

    public async Task<ToolResult> ExecuteAsync(
        Dictionary<string, object> parameters,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        var action = parameters.GetValueOrDefault("action")?.ToString() ?? "";
        var target = parameters.GetValueOrDefault("target")?.ToString();
        var message = parameters.GetValueOrDefault("message")?.ToString();
        var count = 10;

        if (parameters.TryGetValue("count", out var countObj) && int.TryParse(countObj?.ToString(), out var parsedCount))
        {
            count = Math.Min(parsedCount, 100);
        }

        if (string.IsNullOrEmpty(context.ProjectPath))
        {
            return new ToolResult
            {
                Success = false,
                Error = "Project path not set"
            };
        }

        return action.ToLower() switch
        {
            "status" => await RunGitCommandAsync(context.ProjectPath, "status --porcelain", cancellationToken),
            "diff" => await RunGitCommandAsync(context.ProjectPath, $"diff {target ?? ""}", cancellationToken),
            "log" => await RunGitCommandAsync(context.ProjectPath, $"log --oneline -n {count} {target ?? ""}", cancellationToken),
            "branch" => await RunGitCommandAsync(context.ProjectPath, "branch -a", cancellationToken),
            "show" => await RunGitCommandAsync(context.ProjectPath, $"show {target ?? "HEAD"}", cancellationToken),
            "blame" => await RunBlameAsync(context.ProjectPath, target, cancellationToken),
            "create_branch" => await CreateBranchAsync(context.ProjectPath, target, cancellationToken),
            "commit" => await CommitChangesAsync(context.ProjectPath, message, cancellationToken),
            _ => new ToolResult { Success = false, Error = $"Unknown git action: {action}" }
        };
    }

    private async Task<ToolResult> RunGitCommandAsync(string workingDir, string arguments, CancellationToken cancellationToken)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                return new ToolResult
                {
                    Success = false,
                    Error = "Failed to start git process"
                };
            }

            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0 && !string.IsNullOrEmpty(error))
            {
                return new ToolResult
                {
                    Success = false,
                    Output = output,
                    Error = error
                };
            }

            // Limit output size
            if (output.Length > 50000)
            {
                output = output.Substring(0, 50000) + "\n... [truncated]";
            }

            return new ToolResult
            {
                Success = true,
                Output = string.IsNullOrEmpty(output) ? "No output" : output
            };
        }
        catch (Exception ex)
        {
            return new ToolResult
            {
                Success = false,
                Error = $"Git command failed: {ex.Message}"
            };
        }
    }

    private async Task<ToolResult> RunBlameAsync(string workingDir, string? filePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return new ToolResult
            {
                Success = false,
                Error = "File path is required for blame"
            };
        }

        return await RunGitCommandAsync(workingDir, $"blame --line-porcelain {filePath}", cancellationToken);
    }

    private async Task<ToolResult> CreateBranchAsync(string workingDir, string? branchName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(branchName))
        {
            return new ToolResult
            {
                Success = false,
                Error = "Branch name is required"
            };
        }

        // Sanitize branch name
        var safeBranchName = branchName.Replace(" ", "-").Replace("/", "-");
        var result = await RunGitCommandAsync(workingDir, $"checkout -b {safeBranchName}", cancellationToken);

        if (result.Success)
        {
            result.Output = $"Created and switched to branch: {safeBranchName}";
        }

        return result;
    }

    private async Task<ToolResult> CommitChangesAsync(string workingDir, string? message, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(message))
        {
            return new ToolResult
            {
                Success = false,
                Error = "Commit message is required"
            };
        }

        // First stage all changes
        var stageResult = await RunGitCommandAsync(workingDir, "add -A", cancellationToken);
        if (!stageResult.Success)
        {
            return stageResult;
        }

        // Then commit
        var escapedMessage = message.Replace("\"", "\\\"");
        return await RunGitCommandAsync(workingDir, $"commit -m \"{escapedMessage}\"", cancellationToken);
    }
}
