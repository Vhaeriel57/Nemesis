using Nemesis.Shared.Interfaces;

namespace Nemesis.AgentCore.Tools;

public class FileSystemTool : ITool
{
    public string Name => "file_system";
    public string Description => "Read, list, and search files in the project directory. Cannot write or delete files directly.";

    public ToolDefinition Definition => new()
    {
        Name = Name,
        Description = Description,
        Parameters = new Dictionary<string, ToolParameter>
        {
            ["action"] = new()
            {
                Type = "string",
                Description = "The action to perform",
                Enum = new List<string> { "read", "list", "search", "exists" }
            },
            ["path"] = new()
            {
                Type = "string",
                Description = "The file or directory path (relative to project root)"
            },
            ["pattern"] = new()
            {
                Type = "string",
                Description = "Search pattern for list action (e.g., *.cs) or text to search for in search action"
            },
            ["recursive"] = new()
            {
                Type = "boolean",
                Description = "Whether to search recursively (default: true)"
            }
        },
        Required = new List<string> { "action", "path" }
    };

    public async Task<ToolResult> ExecuteAsync(
        Dictionary<string, object> parameters,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        var action = parameters.GetValueOrDefault("action")?.ToString() ?? "";
        var relativePath = parameters.GetValueOrDefault("path")?.ToString() ?? "";
        var pattern = parameters.GetValueOrDefault("pattern")?.ToString();
        var recursive = parameters.GetValueOrDefault("recursive")?.ToString()?.ToLower() != "false";

        if (string.IsNullOrEmpty(context.ProjectPath))
        {
            return new ToolResult
            {
                Success = false,
                Error = "Project path not set"
            };
        }

        var fullPath = Path.GetFullPath(Path.Combine(context.ProjectPath, relativePath));

        // Security check
        if (!IsPathAllowed(context.ProjectPath, fullPath, context.AllowedPaths))
        {
            return new ToolResult
            {
                Success = false,
                Error = "Access denied: Path is outside allowed directories"
            };
        }

        return action.ToLower() switch
        {
            "read" => await ReadFileAsync(fullPath, cancellationToken),
            "list" => await ListDirectoryAsync(fullPath, pattern, recursive, cancellationToken),
            "search" => await SearchFilesAsync(fullPath, pattern ?? "", recursive, cancellationToken),
            "exists" => CheckExists(fullPath),
            _ => new ToolResult { Success = false, Error = $"Unknown action: {action}" }
        };
    }

    private async Task<ToolResult> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return new ToolResult
            {
                Success = false,
                Error = $"File not found: {path}"
            };
        }

        try
        {
            var content = await File.ReadAllTextAsync(path, cancellationToken);

            // Limit content size
            if (content.Length > 100000)
            {
                content = content.Substring(0, 100000) + "\n... [truncated - file too large]";
            }

            return new ToolResult
            {
                Success = true,
                Output = content,
                Data = new { path, lineCount = content.Split('\n').Length }
            };
        }
        catch (Exception ex)
        {
            return new ToolResult
            {
                Success = false,
                Error = $"Failed to read file: {ex.Message}"
            };
        }
    }

    private Task<ToolResult> ListDirectoryAsync(string path, string? pattern, bool recursive, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(path))
        {
            return Task.FromResult(new ToolResult
            {
                Success = false,
                Error = $"Directory not found: {path}"
            });
        }

        try
        {
            var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var searchPattern = pattern ?? "*";

            var files = Directory.GetFiles(path, searchPattern, searchOption)
                .Select(f => Path.GetRelativePath(path, f))
                .Take(500)
                .ToList();

            var dirs = Directory.GetDirectories(path, "*", SearchOption.TopDirectoryOnly)
                .Select(d => Path.GetFileName(d) + "/")
                .ToList();

            return Task.FromResult(new ToolResult
            {
                Success = true,
                Output = $"Directories:\n{string.Join("\n", dirs)}\n\nFiles:\n{string.Join("\n", files)}",
                Data = new { directories = dirs, files }
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ToolResult
            {
                Success = false,
                Error = $"Failed to list directory: {ex.Message}"
            });
        }
    }

    private async Task<ToolResult> SearchFilesAsync(string path, string searchText, bool recursive, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(searchText))
        {
            return new ToolResult
            {
                Success = false,
                Error = "Search pattern is required"
            };
        }

        try
        {
            var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var results = new List<SearchMatch>();

            foreach (var file in Directory.GetFiles(path, "*.cs", searchOption))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var content = await File.ReadAllTextAsync(file, cancellationToken);
                var lines = content.Split('\n');

                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Contains(searchText, StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(new SearchMatch
                        {
                            File = Path.GetRelativePath(path, file),
                            Line = i + 1,
                            Content = lines[i].Trim()
                        });

                        if (results.Count >= 100)
                            break;
                    }
                }

                if (results.Count >= 100)
                    break;
            }

            var output = string.Join("\n", results.Select(r => $"{r.File}:{r.Line}: {r.Content}"));

            return new ToolResult
            {
                Success = true,
                Output = output.Length > 0 ? output : "No matches found",
                Data = results
            };
        }
        catch (Exception ex)
        {
            return new ToolResult
            {
                Success = false,
                Error = $"Search failed: {ex.Message}"
            };
        }
    }

    private ToolResult CheckExists(string path)
    {
        var fileExists = File.Exists(path);
        var dirExists = Directory.Exists(path);

        return new ToolResult
        {
            Success = true,
            Output = fileExists ? "File exists" : (dirExists ? "Directory exists" : "Path does not exist"),
            Data = new { fileExists, directoryExists = dirExists }
        };
    }

    private bool IsPathAllowed(string projectPath, string targetPath, List<string> allowedPaths)
    {
        var normalizedProject = Path.GetFullPath(projectPath).TrimEnd(Path.DirectorySeparatorChar);
        var normalizedTarget = Path.GetFullPath(targetPath);

        if (normalizedTarget.StartsWith(normalizedProject, StringComparison.OrdinalIgnoreCase))
            return true;

        return allowedPaths.Any(allowed =>
            normalizedTarget.StartsWith(Path.GetFullPath(allowed), StringComparison.OrdinalIgnoreCase));
    }

    private class SearchMatch
    {
        public string File { get; set; } = "";
        public int Line { get; set; }
        public string Content { get; set; } = "";
    }
}
