using System.Text;
using Nemesis.CodeAnalysis.Indexing;
using Nemesis.Shared.Interfaces;

namespace Nemesis.AgentCore.Tools;

public class CodeIndexTool : ITool
{
    private readonly CodeIndexer _indexer;

    public string Name => "code_index";
    public string Description => "Search and navigate the indexed codebase. Find types, methods, references, and dependencies.";

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
                Enum = new List<string> { "search", "definition", "references", "dependencies", "file_symbols", "type_graph" }
            },
            ["query"] = new()
            {
                Type = "string",
                Description = "Search query, symbol name, or file path"
            },
            ["max_results"] = new()
            {
                Type = "integer",
                Description = "Maximum number of results (default: 20)"
            }
        },
        Required = new List<string> { "action" }
    };

    public CodeIndexTool(CodeIndexer indexer)
    {
        _indexer = indexer;
    }

    public async Task<ToolResult> ExecuteAsync(
        Dictionary<string, object> parameters,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        if (!_indexer.IsIndexed)
        {
            return new ToolResult
            {
                Success = false,
                Error = "Project not indexed. Please index the project first."
            };
        }

        var action = parameters.GetValueOrDefault("action")?.ToString() ?? "";
        var query = parameters.GetValueOrDefault("query")?.ToString() ?? "";
        var maxResults = 20;

        if (parameters.TryGetValue("max_results", out var maxObj) && int.TryParse(maxObj?.ToString(), out var parsed))
        {
            maxResults = Math.Min(parsed, 100);
        }

        return action.ToLower() switch
        {
            "search" => await SearchSymbolsAsync(query, maxResults, cancellationToken),
            "definition" => await GetDefinitionAsync(query, cancellationToken),
            "references" => await GetReferencesAsync(query, cancellationToken),
            "dependencies" => await GetDependenciesAsync(query, cancellationToken),
            "file_symbols" => await GetFileSymbolsAsync(query, cancellationToken),
            "type_graph" => await GetTypeGraphAsync(cancellationToken),
            _ => new ToolResult { Success = false, Error = $"Unknown action: {action}" }
        };
    }

    private async Task<ToolResult> SearchSymbolsAsync(string query, int maxResults, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(query))
        {
            return new ToolResult
            {
                Success = false,
                Error = "Search query is required"
            };
        }

        var symbols = await _indexer.SearchSymbolsAsync(query, maxResults, cancellationToken);

        var sb = new StringBuilder();
        sb.AppendLine($"Found {symbols.Count} symbols matching '{query}':");
        sb.AppendLine();

        foreach (var symbol in symbols)
        {
            sb.AppendLine($"- **{symbol.FullName}** ({symbol.Kind})");
            sb.AppendLine($"  Location: {symbol.FilePath}:{symbol.StartLine}");
            if (!string.IsNullOrEmpty(symbol.Documentation))
            {
                sb.AppendLine($"  Doc: {symbol.Documentation.Substring(0, Math.Min(100, symbol.Documentation.Length))}...");
            }
            sb.AppendLine();
        }

        return new ToolResult
        {
            Success = true,
            Output = sb.ToString(),
            Data = symbols
        };
    }

    private async Task<ToolResult> GetDefinitionAsync(string symbolName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(symbolName))
        {
            return new ToolResult
            {
                Success = false,
                Error = "Symbol name is required"
            };
        }

        var definition = await _indexer.GetSymbolDefinitionAsync(symbolName, cancellationToken: cancellationToken);

        if (definition == null)
        {
            return new ToolResult
            {
                Success = false,
                Error = $"Symbol '{symbolName}' not found"
            };
        }

        var sb = new StringBuilder();
        sb.AppendLine($"## {definition.FullName}");
        sb.AppendLine($"- Kind: {definition.Kind}");
        sb.AppendLine($"- File: {definition.FilePath}");
        sb.AppendLine($"- Lines: {definition.StartLine}-{definition.EndLine}");

        if (!string.IsNullOrEmpty(definition.Documentation))
        {
            sb.AppendLine($"- Documentation: {definition.Documentation}");
        }

        // Get file content around the symbol
        var content = await _indexer.GetFileContentAsync(definition.FilePath, cancellationToken);
        if (!string.IsNullOrEmpty(content))
        {
            var lines = content.Split('\n');
            var startLine = Math.Max(0, definition.StartLine - 1);
            var endLine = Math.Min(lines.Length, definition.EndLine);
            var codeSnippet = string.Join('\n', lines.Skip(startLine).Take(endLine - startLine));

            sb.AppendLine();
            sb.AppendLine("```csharp");
            sb.AppendLine(codeSnippet);
            sb.AppendLine("```");
        }

        return new ToolResult
        {
            Success = true,
            Output = sb.ToString(),
            Data = definition
        };
    }

    private async Task<ToolResult> GetReferencesAsync(string symbolName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(symbolName))
        {
            return new ToolResult
            {
                Success = false,
                Error = "Symbol name is required"
            };
        }

        var references = await _indexer.GetReferencesAsync(symbolName, cancellationToken);

        if (!references.Any())
        {
            return new ToolResult
            {
                Success = true,
                Output = $"No references found for '{symbolName}'"
            };
        }

        var sb = new StringBuilder();
        sb.AppendLine($"References to '{symbolName}' ({references.Count} found):");
        sb.AppendLine();

        foreach (var reference in references)
        {
            sb.AppendLine($"- {reference.FullName} ({reference.Kind}) at {reference.FilePath}:{reference.StartLine}");
        }

        return new ToolResult
        {
            Success = true,
            Output = sb.ToString(),
            Data = references
        };
    }

    private async Task<ToolResult> GetDependenciesAsync(string filePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return new ToolResult
            {
                Success = false,
                Error = "File path is required"
            };
        }

        // Resolve full path
        var projectPath = _indexer.CurrentProjectPath;
        if (!string.IsNullOrEmpty(projectPath) && !Path.IsPathRooted(filePath))
        {
            filePath = Path.Combine(projectPath, filePath);
        }

        var dependencies = await _indexer.GetDependenciesAsync(filePath, cancellationToken);

        var sb = new StringBuilder();
        sb.AppendLine($"Dependencies for '{Path.GetFileName(filePath)}':");
        sb.AppendLine();

        foreach (var dep in dependencies)
        {
            sb.AppendLine($"- {dep}");
        }

        return new ToolResult
        {
            Success = true,
            Output = sb.ToString(),
            Data = dependencies
        };
    }

    private async Task<ToolResult> GetFileSymbolsAsync(string filePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return new ToolResult
            {
                Success = false,
                Error = "File path is required"
            };
        }

        // Resolve full path
        var projectPath = _indexer.CurrentProjectPath;
        if (!string.IsNullOrEmpty(projectPath) && !Path.IsPathRooted(filePath))
        {
            filePath = Path.Combine(projectPath, filePath);
        }

        var symbols = await _indexer.GetFileSymbolsAsync(filePath, cancellationToken);

        var sb = new StringBuilder();
        sb.AppendLine($"Symbols in '{Path.GetFileName(filePath)}' ({symbols.Count} found):");
        sb.AppendLine();

        foreach (var symbol in symbols)
        {
            var indent = symbol.Kind == "Method" || symbol.Kind == "Property" || symbol.Kind == "Field" ? "  " : "";
            sb.AppendLine($"{indent}- {symbol.Name} ({symbol.Kind}) line {symbol.StartLine}");
        }

        return new ToolResult
        {
            Success = true,
            Output = sb.ToString(),
            Data = symbols
        };
    }

    private async Task<ToolResult> GetTypeGraphAsync(CancellationToken cancellationToken)
    {
        var graph = await _indexer.GetTypeGraphAsync(cancellationToken);

        var sb = new StringBuilder();
        sb.AppendLine("Type inheritance/implementation graph:");
        sb.AppendLine();

        foreach (var (baseType, implementers) in graph.Take(50))
        {
            sb.AppendLine($"**{baseType}**");
            foreach (var impl in implementers.Take(10))
            {
                sb.AppendLine($"  <- {impl}");
            }
            sb.AppendLine();
        }

        if (graph.Count > 50)
        {
            sb.AppendLine($"... and {graph.Count - 50} more types");
        }

        return new ToolResult
        {
            Success = true,
            Output = sb.ToString(),
            Data = graph
        };
    }
}
