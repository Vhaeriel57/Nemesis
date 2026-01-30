using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Nemesis.Shared.DTOs;
using Nemesis.Shared.Interfaces;
using Nemesis.Shared.Models;

namespace Nemesis.AgentCore.Agents;

public abstract class BaseAgent : IAgent
{
    protected readonly ILlmProvider LlmProvider;
    protected readonly Dictionary<string, ITool> Tools;
    protected readonly ILogger Logger;

    public abstract string Name { get; }
    public abstract AgentType Type { get; }
    public abstract string SystemPrompt { get; }
    public abstract List<string> Capabilities { get; }

    protected BaseAgent(
        ILlmProvider llmProvider,
        IEnumerable<ITool> tools,
        ILogger logger)
    {
        LlmProvider = llmProvider;
        Tools = tools.ToDictionary(t => t.Name, t => t);
        Logger = logger;
    }

    public virtual async Task<AgentResponse> ProcessAsync(
        string userMessage,
        AgentContext context,
        CancellationToken cancellationToken = default)
    {
        var response = new AgentResponse();
        var messages = new List<ChatMessage>(context.ChatHistory);
        messages.Add(new ChatMessage { Role = "user", Content = userMessage });

        var contextPrompt = BuildContextPrompt(context);
        var fullSystemPrompt = $"{SystemPrompt}\n\n{contextPrompt}";

        var toolDefs = Tools.Values.Select(t => t.Definition).ToList();
        var maxIterations = 10;
        var iteration = 0;

        while (iteration < maxIterations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            iteration++;

            var llmResponse = await LlmProvider.CompleteWithToolsAsync(
                messages,
                toolDefs,
                fullSystemPrompt,
                0.7,
                4096,
                cancellationToken);

            // Check for tool calls in the response
            var toolCall = ParseToolCall(llmResponse);

            if (toolCall != null)
            {
                response.ToolCalls.Add(toolCall);

                // Execute the tool
                var toolResult = await ExecuteToolAsync(toolCall, context, cancellationToken);
                toolCall.Result = toolResult.Output;
                toolCall.IsComplete = true;

                // Add tool interaction to messages
                messages.Add(new ChatMessage
                {
                    Role = "assistant",
                    Content = llmResponse,
                    ToolCalls = new List<ToolCall> { toolCall }
                });

                messages.Add(new ChatMessage
                {
                    Role = "tool",
                    Content = $"Tool '{toolCall.Name}' result:\n{toolResult.Output}",
                    Metadata = new Dictionary<string, object>
                    {
                        ["tool_call_id"] = toolCall.Id,
                        ["success"] = toolResult.Success
                    }
                });

                // Check if this was a patch creation
                if (toolCall.Name == "patch" && toolResult.Data is FilePatch patch)
                {
                    response.GeneratedPatches ??= new PatchSet { Description = "Agent-generated patches" };
                    response.GeneratedPatches.Patches.Add(patch);
                }
            }
            else
            {
                // No more tool calls, we have the final response
                response.Content = llmResponse;
                break;
            }
        }

        // Extract any citations from web search results
        response.Citations = ExtractCitations(response.ToolCalls);

        return response;
    }

    public virtual async IAsyncEnumerable<AgentStreamEvent> ProcessStreamAsync(
        string userMessage,
        AgentContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var contextPrompt = BuildContextPrompt(context);
        var fullPrompt = BuildFullPrompt(userMessage, context.ChatHistory, contextPrompt);

        await foreach (var chunk in LlmProvider.StreamCompleteAsync(
            fullPrompt,
            SystemPrompt,
            0.7,
            4096,
            cancellationToken))
        {
            yield return new AgentStreamEvent
            {
                Type = AgentStreamEventType.TextDelta,
                Content = chunk
            };
        }

        yield return new AgentStreamEvent { Type = AgentStreamEventType.Complete };
    }

    protected virtual string BuildContextPrompt(AgentContext context)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrEmpty(context.ProjectPath))
        {
            sb.AppendLine($"## Project Context");
            sb.AppendLine($"Project path: {context.ProjectPath}");
            sb.AppendLine();
        }

        if (context.RelevantFiles.Any())
        {
            sb.AppendLine("## Relevant Files");
            foreach (var (path, content) in context.RelevantFiles.Take(5))
            {
                sb.AppendLine($"### {path}");
                sb.AppendLine("```csharp");
                sb.AppendLine(content.Length > 2000 ? content.Substring(0, 2000) + "\n// ... truncated" : content);
                sb.AppendLine("```");
                sb.AppendLine();
            }
        }

        if (context.RelevantSymbols.Any())
        {
            sb.AppendLine("## Relevant Symbols");
            foreach (var symbol in context.RelevantSymbols.Take(10))
            {
                sb.AppendLine($"- {symbol.FullName} ({symbol.Kind}) at {symbol.FilePath}:{symbol.StartLine}");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    protected virtual string BuildFullPrompt(string userMessage, List<ChatMessage> history, string contextPrompt)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrEmpty(contextPrompt))
        {
            sb.AppendLine(contextPrompt);
            sb.AppendLine();
        }

        // Include recent history
        foreach (var msg in history.TakeLast(10))
        {
            var role = msg.Role == "user" ? "User" : "Assistant";
            sb.AppendLine($"{role}: {msg.Content}");
            sb.AppendLine();
        }

        sb.AppendLine($"User: {userMessage}");

        return sb.ToString();
    }

    protected virtual ToolCall? ParseToolCall(string response)
    {
        // Look for JSON tool call in the response
        var jsonMatch = Regex.Match(response, @"```json\s*(\{[\s\S]*?\})\s*```", RegexOptions.IgnoreCase);
        if (!jsonMatch.Success)
        {
            jsonMatch = Regex.Match(response, @"\{""tool"":\s*""[^""]+""[\s\S]*?\}", RegexOptions.IgnoreCase);
        }

        if (!jsonMatch.Success)
            return null;

        try
        {
            var json = jsonMatch.Groups[1].Success ? jsonMatch.Groups[1].Value : jsonMatch.Value;
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("tool", out var toolProp))
                return null;

            var toolName = toolProp.GetString();
            if (string.IsNullOrEmpty(toolName) || !Tools.ContainsKey(toolName))
                return null;

            var parameters = "{}";
            if (root.TryGetProperty("parameters", out var paramsProp))
            {
                parameters = paramsProp.GetRawText();
            }

            return new ToolCall
            {
                Name = toolName,
                Arguments = parameters
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    protected virtual async Task<ToolResult> ExecuteToolAsync(
        ToolCall toolCall,
        AgentContext context,
        CancellationToken cancellationToken)
    {
        if (!Tools.TryGetValue(toolCall.Name, out var tool))
        {
            return new ToolResult
            {
                Success = false,
                Error = $"Unknown tool: {toolCall.Name}"
            };
        }

        try
        {
            var parameters = JsonSerializer.Deserialize<Dictionary<string, object>>(toolCall.Arguments)
                ?? new Dictionary<string, object>();

            // Convert JsonElements to proper types
            var convertedParams = new Dictionary<string, object>();
            foreach (var (key, value) in parameters)
            {
                if (value is JsonElement element)
                {
                    convertedParams[key] = element.ValueKind switch
                    {
                        JsonValueKind.String => element.GetString() ?? "",
                        JsonValueKind.Number => element.TryGetInt32(out var i) ? i : element.GetDouble(),
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        _ => element.GetRawText()
                    };
                }
                else
                {
                    convertedParams[key] = value;
                }
            }

            var toolContext = new ToolContext
            {
                ProjectPath = context.ProjectPath,
                CachePath = "./cache",
                NetworkEnabled = true,
                AllowedPaths = new List<string> { context.ProjectPath, "./cache", "./backups" }
            };

            Logger.LogInformation("Executing tool {Tool} with parameters: {Params}",
                toolCall.Name, toolCall.Arguments);

            return await tool.ExecuteAsync(convertedParams, toolContext, cancellationToken);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Tool execution failed: {Tool}", toolCall.Name);
            return new ToolResult
            {
                Success = false,
                Error = $"Tool execution failed: {ex.Message}"
            };
        }
    }

    protected virtual List<string> ExtractCitations(List<ToolCall> toolCalls)
    {
        var citations = new List<string>();

        foreach (var call in toolCalls.Where(c => c.Name == "web_search" && c.Result != null))
        {
            var urlMatches = Regex.Matches(call.Result!, @"URL:\s*(https?://[^\s]+)");
            foreach (Match match in urlMatches)
            {
                citations.Add(match.Groups[1].Value);
            }
        }

        return citations.Distinct().ToList();
    }
}
