using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nemesis.Shared.DTOs;
using Nemesis.Shared.Interfaces;
using Nemesis.Shared.Models;

namespace Nemesis.AgentCore.Agents;

/// <summary>
/// Manager agent that orchestrates other specialized agents.
/// Acts as a team lead, delegating tasks and synthesizing responses.
/// </summary>
public class ManagerAgent : BaseAgent
{
    private readonly Dictionary<AgentType, IAgent> _teamAgents;

    public override string Name => "Project Manager";
    public override AgentType Type => AgentType.Manager;

    public override string SystemPrompt => @"You are a Project Manager AI coordinating a team of specialized AI agents for Unity game development.

## Your Team
- **Unity Expert** (SeniorUnityCSharp): Architecture, performance optimization, Unity 6 patterns, complex C# code
- **Generalist**: General questions, explanations, documentation, simple tasks
- **Researcher**: Web searches, finding documentation, looking up solutions online

## Your Role
1. Analyze the user's request
2. Decide which team member(s) should handle it
3. Delegate tasks to appropriate agents
4. Synthesize their responses into a coherent answer
5. Ensure quality and completeness

## Delegation Format
When you need to delegate, output JSON:
```json
{
  ""action"": ""delegate"",
  ""agent"": ""SeniorUnityCSharp"" | ""Generalist"" | ""Researcher"",
  ""task"": ""Clear description of what this agent should do"",
  ""context"": ""Any relevant context from the conversation""
}
```

## Guidelines
- For code-heavy tasks → Unity Expert
- For explanations/Q&A → Generalist
- For external info needs → Researcher
- Complex tasks may need multiple agents
- Always synthesize responses, don't just pass them through
- Add your own insights and recommendations

## Response Format
After receiving agent responses, provide a final synthesis:
1. Summary of what was done
2. Key findings/solutions
3. Recommendations
4. Any follow-up actions needed";

    public override List<string> Capabilities => new()
    {
        "Team coordination",
        "Task delegation",
        "Response synthesis",
        "Quality assurance",
        "Project oversight"
    };

    public ManagerAgent(
        ILlmProvider llmProvider,
        IEnumerable<ITool> tools,
        IEnumerable<IAgent> teamAgents,
        ILogger<ManagerAgent> logger)
        : base(llmProvider, tools, logger)
    {
        _teamAgents = teamAgents
            .Where(a => a.Type != AgentType.Manager)
            .ToDictionary(a => a.Type, a => a);
    }

    public override async Task<AgentResponse> ProcessAsync(
        string userMessage,
        AgentContext context,
        CancellationToken cancellationToken = default)
    {
        var response = new AgentResponse();
        var conversationLog = new StringBuilder();
        var agentResponses = new List<(AgentType Agent, string Response)>();

        // Phase 1: Initial analysis and delegation decision
        var analysisPrompt = $@"User request: {userMessage}

Analyze this request and decide how to handle it:
1. Can you answer directly? If simple, answer now.
2. Need delegation? Output the delegation JSON.
3. Need multiple agents? You can delegate sequentially.

Project context: {context.ProjectPath ?? "No project loaded"}";

        var messages = new List<ChatMessage>(context.ChatHistory);
        messages.Add(new ChatMessage { Role = "user", Content = analysisPrompt });

        var maxDelegations = 5;
        var delegationCount = 0;
        var currentResponse = "";

        while (delegationCount < maxDelegations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var llmResponse = await LlmProvider.CompleteAsync(
                messages,
                SystemPrompt,
                0.7,
                4096,
                cancellationToken);

            currentResponse = llmResponse;

            // Check for delegation
            var delegation = ParseDelegation(llmResponse);

            if (delegation == null)
            {
                // No delegation, this is the final response
                break;
            }

            delegationCount++;

            // Execute delegation
            var agentType = Enum.Parse<AgentType>(delegation.Agent, ignoreCase: true);
            if (!_teamAgents.TryGetValue(agentType, out var agent))
            {
                Logger.LogWarning("Unknown agent type for delegation: {Agent}", delegation.Agent);
                continue;
            }

            Logger.LogInformation("Manager delegating to {Agent}: {Task}",
                delegation.Agent, delegation.Task);

            // Record delegation in conversation
            conversationLog.AppendLine($"\n### Manager → {agent.Name}");
            conversationLog.AppendLine($"Task: {delegation.Task}");

            // Create context for sub-agent
            var agentContext = new AgentContext
            {
                ProjectPath = context.ProjectPath,
                ChatHistory = new List<ChatMessage>(),
                RelevantFiles = context.RelevantFiles,
                RelevantSymbols = context.RelevantSymbols
            };

            // Execute sub-agent
            var agentResult = await agent.ProcessAsync(
                delegation.Task + (string.IsNullOrEmpty(delegation.Context) ? "" : $"\n\nContext: {delegation.Context}"),
                agentContext,
                cancellationToken);

            // Collect response
            agentResponses.Add((agentType, agentResult.Content));

            // Add any patches generated
            if (agentResult.GeneratedPatches != null)
            {
                response.GeneratedPatches ??= new PatchSet { Description = "Manager-coordinated patches" };
                response.GeneratedPatches.Patches.AddRange(agentResult.GeneratedPatches.Patches);
            }

            // Add tool calls
            response.ToolCalls.AddRange(agentResult.ToolCalls);

            // Record response
            conversationLog.AppendLine($"\n### {agent.Name} Response:");
            conversationLog.AppendLine(agentResult.Content.Length > 500
                ? agentResult.Content.Substring(0, 500) + "..."
                : agentResult.Content);

            // Add agent response to messages for synthesis
            messages.Add(new ChatMessage
            {
                Role = "assistant",
                Content = llmResponse
            });
            messages.Add(new ChatMessage
            {
                Role = "user",
                Content = $"Response from {agent.Name}:\n\n{agentResult.Content}\n\nContinue coordinating or provide final synthesis."
            });
        }

        // Phase 2: Synthesize if we had delegations
        if (agentResponses.Any())
        {
            var synthesisPrompt = $@"You delegated to the following agents and received their responses:

{string.Join("\n\n", agentResponses.Select(r => $"**{r.Agent}**: {r.Response}"))}

Now provide a final, synthesized response to the user's original request:
""{userMessage}""

Include:
1. Summary of findings
2. Concrete recommendations
3. Any code or solutions from the team
4. Next steps if applicable";

            messages.Add(new ChatMessage { Role = "user", Content = synthesisPrompt });

            var synthesis = await LlmProvider.CompleteAsync(
                messages,
                SystemPrompt,
                0.7,
                4096,
                cancellationToken);

            response.Content = synthesis;
        }
        else
        {
            response.Content = currentResponse;
        }

        // Add conversation log to metadata
        response.Metadata ??= new Dictionary<string, object>();
        response.Metadata["conversation_log"] = conversationLog.ToString();
        response.Metadata["delegations"] = agentResponses.Select(r => new { Agent = r.Agent.ToString(), ResponsePreview = r.Response.Substring(0, Math.Min(200, r.Response.Length)) }).ToList();

        return response;
    }

    public override async IAsyncEnumerable<AgentStreamEvent> ProcessStreamAsync(
        string userMessage,
        AgentContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // For streaming, we'll emit events for each phase
        yield return new AgentStreamEvent
        {
            Type = AgentStreamEventType.AgentThinking,
            Content = "Manager analyzing request...",
            AgentType = AgentType.Manager
        };

        var response = await ProcessAsync(userMessage, context, cancellationToken);

        // Emit delegation info if any
        if (response.Metadata?.TryGetValue("delegations", out var delegations) == true)
        {
            yield return new AgentStreamEvent
            {
                Type = AgentStreamEventType.Delegation,
                Content = JsonSerializer.Serialize(delegations),
                AgentType = AgentType.Manager
            };
        }

        // Stream the final response
        foreach (var chunk in ChunkText(response.Content, 50))
        {
            yield return new AgentStreamEvent
            {
                Type = AgentStreamEventType.TextDelta,
                Content = chunk
            };
            await Task.Delay(10, cancellationToken); // Small delay for visual effect
        }

        yield return new AgentStreamEvent { Type = AgentStreamEventType.Complete };
    }

    private DelegationRequest? ParseDelegation(string response)
    {
        try
        {
            // Look for delegation JSON
            var jsonMatch = System.Text.RegularExpressions.Regex.Match(
                response,
                @"\{[\s\S]*?""action""\s*:\s*""delegate""[\s\S]*?\}",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (!jsonMatch.Success)
                return null;

            var json = jsonMatch.Value;
            return JsonSerializer.Deserialize<DelegationRequest>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch
        {
            return null;
        }
    }

    private IEnumerable<string> ChunkText(string text, int chunkSize)
    {
        for (int i = 0; i < text.Length; i += chunkSize)
        {
            yield return text.Substring(i, Math.Min(chunkSize, text.Length - i));
        }
    }

    private class DelegationRequest
    {
        public string Action { get; set; } = "";
        public string Agent { get; set; } = "";
        public string Task { get; set; } = "";
        public string? Context { get; set; }
    }
}
