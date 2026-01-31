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

    public override string SystemPrompt => @"Tu es le Manager de Nemesis, un assistant IA expert en développement de jeux Unity.

## RÈGLE ABSOLUE
Tu DOIS TOUJOURS répondre en FRANÇAIS, peu importe la langue de la question.

## Ton Équipe
- **Expert Unity** (SeniorUnityCSharp): Architecture, optimisation, patterns Unity/C#
- **Généraliste** (Generalist): Explications, documentation, questions générales
- **Chercheur** (Researcher): Recherches web, documentation externe

## Ta Mission
Quand l'utilisateur te parle de son projet:
1. ANALYSE le contexte fourni (fichiers, types, symboles)
2. RÉPONDS de manière pertinente et personnalisée
3. DÉLÈGUE seulement si une expertise spécifique est nécessaire

## Format de Délégation (optionnel)
```json
{""action"":""delegate"",""agent"":""SeniorUnityCSharp"",""task"":""description""}
```

## Style de Réponse
- Sois conversationnel et amical
- Utilise le Markdown pour structurer
- Base-toi sur les fichiers du projet fournis dans le contexte
- Donne des conseils concrets et actionnables
- Si tu vois des patterns ou problèmes, mentionne-les

## INTERDIT
- Répondre en anglais
- Ignorer le contexte du projet
- Inventer des informations";

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

        // Build rich project context
        var projectContext = BuildProjectContext(context);

        // Phase 1: Initial analysis and delegation decision
        var analysisPrompt = $@"## Demande de l'utilisateur
{userMessage}

## Contexte du Projet
{projectContext}

## Instructions
Réponds DIRECTEMENT en français à la demande de l'utilisateur.
- Utilise le contexte du projet ci-dessus pour personnaliser ta réponse
- Si la question concerne le projet, analyse les fichiers listés
- Ne délègue que si tu as besoin d'une expertise très spécifique
- Sois conversationnel, utile et concret";

        var messages = new List<ChatMessage>(context.ChatHistory);
        messages.Add(new ChatMessage { Role = "user", Content = analysisPrompt });

        var maxDelegations = 5;
        var delegationCount = 0;
        var currentResponse = "";

        while (delegationCount < maxDelegations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var prompt = FormatMessagesAsPrompt(messages);
            var llmResponse = await LlmProvider.CompleteAsync(
                prompt,
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
            var synthesisPrompt = $@"Tu as délégué aux agents suivants et reçu leurs réponses:

{string.Join("\n\n", agentResponses.Select(r => $"**{r.Agent}**: {r.Response}"))}

Fournis maintenant une réponse finale et synthétisée à la demande de l'utilisateur:
""{userMessage}""

Ta réponse doit être EN FRANÇAIS et inclure:
1. Résumé des trouvailles
2. Recommandations concrètes
3. Code ou solutions de l'équipe
4. Prochaines étapes si applicable";

            messages.Add(new ChatMessage { Role = "user", Content = synthesisPrompt });

            var synthesisFullPrompt = FormatMessagesAsPrompt(messages);
            var synthesis = await LlmProvider.CompleteAsync(
                synthesisFullPrompt,
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

    private string BuildProjectContext(AgentContext context)
    {
        var sb = new StringBuilder();

        // Project path
        if (!string.IsNullOrEmpty(context.ProjectPath))
        {
            sb.AppendLine($"**Chemin du projet**: {context.ProjectPath}");
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("**Aucun projet chargé** - L'utilisateur n'a pas encore sélectionné de projet.");
            return sb.ToString();
        }

        // Relevant files from RAG
        if (context.RelevantFiles.Any())
        {
            sb.AppendLine("### Fichiers pertinents trouvés:");
            foreach (var (filePath, content) in context.RelevantFiles.Take(5))
            {
                var fileName = Path.GetFileName(filePath);
                var preview = content.Length > 500 ? content.Substring(0, 500) + "..." : content;
                sb.AppendLine($"\n**{fileName}** (`{filePath}`):");
                sb.AppendLine("```csharp");
                sb.AppendLine(preview);
                sb.AppendLine("```");
            }
        }
        else
        {
            sb.AppendLine("*Aucun fichier spécifique identifié pour cette requête.*");
        }

        // Relevant symbols
        if (context.RelevantSymbols.Any())
        {
            sb.AppendLine("\n### Types et symboles identifiés:");
            foreach (var symbol in context.RelevantSymbols.Take(10))
            {
                sb.AppendLine($"- **{symbol.Name}** ({symbol.Kind}) dans `{symbol.FilePath}`");
            }
        }

        return sb.ToString();
    }

    private IEnumerable<string> ChunkText(string text, int chunkSize)
    {
        for (int i = 0; i < text.Length; i += chunkSize)
        {
            yield return text.Substring(i, Math.Min(chunkSize, text.Length - i));
        }
    }

    private string FormatMessagesAsPrompt(List<ChatMessage> messages)
    {
        var sb = new StringBuilder();
        foreach (var msg in messages)
        {
            var role = msg.Role?.ToLowerInvariant() switch
            {
                "system" => "System",
                "assistant" => "Assistant",
                _ => "User"
            };
            sb.AppendLine($"{role}: {msg.Content}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private class DelegationRequest
    {
        public string Action { get; set; } = "";
        public string Agent { get; set; } = "";
        public string Task { get; set; } = "";
        public string? Context { get; set; }
    }
}
