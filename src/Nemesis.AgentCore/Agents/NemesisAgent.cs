using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Nemesis.Shared.DTOs;
using Nemesis.Shared.Interfaces;
using Nemesis.Shared.Models;

namespace Nemesis.AgentCore.Agents;

/// <summary>
/// Nemesis - Agent unifié expert en développement Unity/C#
/// Capable de lire le projet, réfléchir, rechercher et générer du code complet
/// </summary>
public class NemesisAgent : BaseAgent
{
    public override string Name => "Nemesis";
    public override AgentType Type => AgentType.Manager; // Use Manager type for compatibility

    public override string SystemPrompt => @"Tu es **Nemesis**, un assistant IA expert en développement de jeux Unity.

## Qui Tu Es
Tu es un développeur senior passionné avec une expertise approfondie en:
- **Unity 6** et URP (Universal Render Pipeline)
- **C# avancé** (async/await, LINQ, patterns, performance)
- **Netcode for GameObjects** (multijoueur, synchronisation, RPC)
- **Architecture de jeux** (design patterns, SOLID, ECS)

## Comment Tu Communiques
- Tu parles **TOUJOURS en français** de manière naturelle et conversationnelle
- Tu comprends le contexte même si les questions ne sont pas précises
- Tu es amical mais professionnel, comme un collègue expérimenté
- Tu expliques tes raisonnements de manière claire

## Ta Méthode de Travail

### 1. COMPRENDRE
Avant de répondre, tu analyses:
- Ce que l'utilisateur veut vraiment accomplir
- Le contexte du projet (fichiers, architecture, conventions)
- Les dépendances et relations entre les scripts

### 2. EXPLORER
Tu utilises ACTIVEMENT tes outils pour:
- **Lire les fichiers** du projet avec `file_system` (action: read_file)
- **Chercher des symboles** avec `code_index` (action: search)
- **Comprendre les relations** entre les scripts
- **Vérifier** ce qui existe déjà avant de proposer du nouveau code

### 3. RÉFLÉCHIR
Tu raisonnes de manière autonome:
- Si une fonctionnalité manque, tu le signales et proposes de la créer
- Tu anticipes les problèmes potentiels
- Tu considères la cohérence avec le code existant

### 4. PROPOSER
Tu génères du code **COMPLET et COHÉRENT**:
- Scripts entiers prêts à l'emploi
- Modifications précises avec numéros de lignes
- Patches au format diff unifié pour l'onglet Patches

## Format de Tes Réponses

### Pour les explications
```markdown
## Analyse
[Ton analyse du problème]

## Ce que j'ai trouvé dans ton projet
[Fichiers consultés et observations]

## Ma proposition
[Solution détaillée]
```

### Pour le code
```csharp
// Utilise des blocs de code avec le langage spécifié
public class Example : MonoBehaviour
{
    // Code complet et commenté
}
```

### Pour les modifications (Patches)
```diff
--- a/Assets/Scripts/Player/PlayerController.cs
+++ b/Assets/Scripts/Player/PlayerController.cs
@@ -10,6 +10,10 @@ public class PlayerController : MonoBehaviour
     // Contexte existant
+    // Nouvelles lignes ajoutées
```

## Utilisation des Outils

Tu as accès à ces outils - UTILISE-LES SYSTÉMATIQUEMENT:

### `code_index` - Recherche dans le projet
- `search`: Chercher des types, méthodes, classes
- `definition`: Obtenir la définition complète d'un symbole
- `references`: Trouver où un symbole est utilisé
- `file_symbols`: Lister les symboles d'un fichier

### `file_system` - Lecture de fichiers
- `read_file`: Lire le contenu complet d'un fichier
- `list_files`: Lister les fichiers d'un dossier

### `patch` - Génération de patches
- `create`: Créer un patch pour modification de fichier
- `create_new`: Créer un nouveau fichier

### `web_search` - Recherche web
- Pour la documentation Unity, solutions StackOverflow, etc.

## Règles Absolues
1. **TOUJOURS** lire les fichiers pertinents avant de proposer du code
2. **JAMAIS** inventer du code sans connaître le contexte existant
3. **TOUJOURS** proposer du code complet et fonctionnel
4. **TOUJOURS** répondre en français de manière conversationnelle
5. **TOUJOURS** citer les fichiers que tu as consultés

## Exemple d'Interaction

**Utilisateur**: ""Je veux que mon possédé puisse fermer les portes""

**Toi**:
1. Tu cherches ""Possédé"", ""Door"", ""Player"" dans le projet
2. Tu lis les fichiers trouvés pour comprendre l'architecture
3. Tu identifies ce qui existe et ce qui manque
4. Tu proposes une solution complète avec:
   - Analyse de l'existant
   - Script complet si nouveau fichier nécessaire
   - Ou modifications précises avec patch si fichier existant";

    public override List<string> Capabilities => new()
    {
        "Expert Unity 6 et URP",
        "Expert C# avancé",
        "Netcode for GameObjects",
        "Lecture et analyse de projet complet",
        "Génération de code cohérent",
        "Création de patches",
        "Recherche web",
        "Raisonnement autonome"
    };

    public NemesisAgent(
        ILlmProvider llmProvider,
        IEnumerable<ITool> tools,
        ILogger<NemesisAgent> logger)
        : base(llmProvider, tools, logger)
    {
    }

    public override async Task<AgentResponse> ProcessAsync(
        string userMessage,
        AgentContext context,
        CancellationToken cancellationToken = default)
    {
        var response = new AgentResponse();

        // Build comprehensive project context
        var projectContext = BuildEnhancedProjectContext(context);

        // Create the main prompt with project context
        var mainPrompt = $@"## Message de l'utilisateur
{userMessage}

## Contexte du Projet
{projectContext}

## Instructions
1. Analyse la demande et le contexte du projet
2. Si tu as besoin de plus d'informations sur des fichiers spécifiques, utilise les outils
3. Réponds de manière complète et conversationnelle en français
4. Si du code est demandé, fournis du code COMPLET et fonctionnel
5. Cite toujours les fichiers que tu as consultés";

        // Build messages list
        var messages = BuildMessagesList(context.ChatHistory, mainPrompt);

        // First, let the agent reason and potentially use tools
        var thinkingPrompt = FormatMessagesForLlm(messages);

        // Call LLM with tools available
        var toolDefinitions = GetToolDefinitions();
        var llmResponse = await LlmProvider.CompleteWithToolsAsync(
            messages,
            toolDefinitions,
            SystemPrompt,
            0.3, // Lower temperature for more consistent responses
            8192, // Larger context for complete code
            cancellationToken);

        // Process any tool calls in the response
        var (finalResponse, toolCalls) = await ProcessToolCalls(
            llmResponse,
            messages,
            context,
            cancellationToken);

        response.Content = finalResponse;
        response.ToolCalls = toolCalls;

        // Extract any patches from the response
        var patches = ExtractPatchesFromResponse(finalResponse, context.ProjectPath);
        if (patches.Any())
        {
            response.GeneratedPatches = new PatchSet
            {
                Description = "Patches générés par Nemesis",
                Patches = patches
            };
        }

        return response;
    }

    private List<ChatMessage> BuildMessagesList(List<ChatMessage> history, string currentPrompt)
    {
        var messages = new List<ChatMessage>();

        // Add relevant history (last 10 messages for context)
        foreach (var msg in history.TakeLast(10))
        {
            messages.Add(new ChatMessage
            {
                Role = msg.Role,
                Content = msg.Content
            });
        }

        // Add current message
        messages.Add(new ChatMessage
        {
            Role = "user",
            Content = currentPrompt
        });

        return messages;
    }

    private string FormatMessagesForLlm(List<ChatMessage> messages)
    {
        var sb = new StringBuilder();
        foreach (var msg in messages)
        {
            var role = msg.Role?.ToLowerInvariant() switch
            {
                "system" => "Système",
                "assistant" => "Nemesis",
                _ => "Utilisateur"
            };
            sb.AppendLine($"**{role}**: {msg.Content}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private List<ToolDefinition> GetToolDefinitions()
    {
        var definitions = new List<ToolDefinition>();

        foreach (var tool in Tools)
        {
            definitions.Add(new ToolDefinition
            {
                Name = tool.Name,
                Description = tool.Description,
                Parameters = tool.Parameters.ToDictionary(
                    p => p.Name,
                    p => new ToolParameter
                    {
                        Type = p.Type,
                        Description = p.Description,
                        Enum = p.AllowedValues
                    }),
                Required = tool.Parameters.Where(p => p.Required).Select(p => p.Name).ToList()
            });
        }

        return definitions;
    }

    private async Task<(string Response, List<ToolCall> ToolCalls)> ProcessToolCalls(
        string llmResponse,
        List<ChatMessage> messages,
        AgentContext context,
        CancellationToken cancellationToken)
    {
        var toolCalls = new List<ToolCall>();
        var currentResponse = llmResponse;
        var iterations = 0;
        var maxIterations = 5; // Limit tool call iterations

        while (iterations < maxIterations)
        {
            // Try to parse tool call from response
            var toolCall = ParseToolCall(currentResponse);
            if (toolCall == null)
                break;

            iterations++;
            Logger.LogInformation("Nemesis executing tool: {Tool} with action: {Action}",
                toolCall.ToolName, toolCall.Parameters.GetValueOrDefault("action"));

            // Execute the tool
            var tool = Tools.FirstOrDefault(t =>
                t.Name.Equals(toolCall.ToolName, StringComparison.OrdinalIgnoreCase));

            if (tool != null)
            {
                try
                {
                    var result = await tool.ExecuteAsync(toolCall.Parameters, cancellationToken);
                    toolCall.Result = result.Success ? result.Output : result.ErrorMessage;
                    toolCalls.Add(toolCall);

                    // Add tool result to messages and continue
                    messages.Add(new ChatMessage
                    {
                        Role = "assistant",
                        Content = currentResponse
                    });
                    messages.Add(new ChatMessage
                    {
                        Role = "user",
                        Content = $"Résultat de l'outil {toolCall.ToolName}:\n```\n{toolCall.Result}\n```\n\nContinue ta réponse en utilisant ces informations."
                    });

                    // Get next response
                    var toolDefinitions = GetToolDefinitions();
                    currentResponse = await LlmProvider.CompleteWithToolsAsync(
                        messages,
                        toolDefinitions,
                        SystemPrompt,
                        0.3,
                        8192,
                        cancellationToken);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Tool execution failed: {Tool}", toolCall.ToolName);
                    toolCall.Result = $"Erreur: {ex.Message}";
                    toolCalls.Add(toolCall);
                    break;
                }
            }
            else
            {
                Logger.LogWarning("Unknown tool requested: {Tool}", toolCall.ToolName);
                break;
            }
        }

        // Clean up any remaining tool call JSON from final response
        currentResponse = CleanToolCallsFromResponse(currentResponse);

        return (currentResponse, toolCalls);
    }

    private ToolCall? ParseToolCall(string response)
    {
        try
        {
            // Look for JSON tool call pattern
            var jsonMatch = Regex.Match(
                response,
                @"\{[\s\S]*?""tool""\s*:\s*""([^""]+)""[\s\S]*?""parameters""\s*:\s*\{([^}]+)\}[\s\S]*?\}",
                RegexOptions.IgnoreCase);

            if (!jsonMatch.Success)
                return null;

            // Try to parse the full JSON
            var startIndex = response.IndexOf('{');
            var depth = 0;
            var endIndex = startIndex;

            for (int i = startIndex; i < response.Length; i++)
            {
                if (response[i] == '{') depth++;
                else if (response[i] == '}') depth--;

                if (depth == 0)
                {
                    endIndex = i;
                    break;
                }
            }

            var jsonStr = response.Substring(startIndex, endIndex - startIndex + 1);
            var jsonDoc = JsonDocument.Parse(jsonStr);
            var root = jsonDoc.RootElement;

            if (!root.TryGetProperty("tool", out var toolProp))
                return null;

            var toolCall = new ToolCall
            {
                ToolName = toolProp.GetString() ?? "",
                Parameters = new Dictionary<string, string>()
            };

            if (root.TryGetProperty("parameters", out var paramsProp))
            {
                foreach (var param in paramsProp.EnumerateObject())
                {
                    toolCall.Parameters[param.Name] = param.Value.ToString();
                }
            }

            return toolCall;
        }
        catch
        {
            return null;
        }
    }

    private string CleanToolCallsFromResponse(string response)
    {
        // Remove JSON tool call blocks from the response
        var cleaned = Regex.Replace(
            response,
            @"\{[\s\S]*?""tool""\s*:\s*""[^""]+""[\s\S]*?\}",
            "",
            RegexOptions.IgnoreCase);

        return cleaned.Trim();
    }

    private string BuildEnhancedProjectContext(AgentContext context)
    {
        var sb = new StringBuilder();

        if (string.IsNullOrEmpty(context.ProjectPath))
        {
            sb.AppendLine("**Aucun projet chargé** - Demande à l'utilisateur de charger un projet Unity.");
            return sb.ToString();
        }

        sb.AppendLine($"**Projet**: `{context.ProjectPath}`");
        sb.AppendLine();

        // Add relevant files with more content
        if (context.RelevantFiles.Any())
        {
            sb.AppendLine("### Fichiers pertinents identifiés:");
            foreach (var (filePath, content) in context.RelevantFiles.Take(8))
            {
                var fileName = Path.GetFileName(filePath);
                var relativePath = GetRelativePath(filePath, context.ProjectPath);

                // Include more content for better understanding
                var preview = content.Length > 1500 ? content.Substring(0, 1500) + "\n// ... (fichier tronqué)" : content;

                sb.AppendLine($"\n#### `{relativePath}`");
                sb.AppendLine("```csharp");
                sb.AppendLine(preview);
                sb.AppendLine("```");
            }
        }

        // Add symbols found
        if (context.RelevantSymbols.Any())
        {
            sb.AppendLine("\n### Types et classes identifiés:");
            var groupedSymbols = context.RelevantSymbols
                .GroupBy(s => s.Kind)
                .OrderBy(g => g.Key);

            foreach (var group in groupedSymbols)
            {
                sb.AppendLine($"\n**{group.Key}s**:");
                foreach (var symbol in group.Take(10))
                {
                    var relativePath = GetRelativePath(symbol.FilePath, context.ProjectPath);
                    sb.AppendLine($"- `{symbol.Name}` dans `{relativePath}`");
                }
            }
        }

        sb.AppendLine("\n---");
        sb.AppendLine("*Tu peux utiliser les outils pour lire d'autres fichiers si nécessaire.*");

        return sb.ToString();
    }

    private string GetRelativePath(string fullPath, string basePath)
    {
        if (string.IsNullOrEmpty(basePath))
            return fullPath;

        try
        {
            var baseUri = new Uri(basePath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
            var fullUri = new Uri(fullPath);
            return Uri.UnescapeDataString(baseUri.MakeRelativeUri(fullUri).ToString())
                .Replace('/', Path.DirectorySeparatorChar);
        }
        catch
        {
            return Path.GetFileName(fullPath);
        }
    }

    private List<FilePatch> ExtractPatchesFromResponse(string response, string? projectPath)
    {
        var patches = new List<FilePatch>();

        // Look for diff blocks
        var diffPattern = @"```diff\s*([\s\S]*?)```";
        var matches = Regex.Matches(response, diffPattern);

        foreach (Match match in matches)
        {
            var diffContent = match.Groups[1].Value.Trim();
            var patch = ParseDiffToPatch(diffContent, projectPath);
            if (patch != null)
            {
                patches.Add(patch);
            }
        }

        // Also look for new file blocks with file path comments
        var newFilePattern = @"```csharp\s*//\s*(?:File|Fichier|Path):\s*([^\n]+)\s*([\s\S]*?)```";
        var newFileMatches = Regex.Matches(response, newFilePattern, RegexOptions.IgnoreCase);

        foreach (Match match in newFileMatches)
        {
            var filePath = match.Groups[1].Value.Trim();
            var content = match.Groups[2].Value.Trim();

            if (!string.IsNullOrEmpty(filePath) && !string.IsNullOrEmpty(content))
            {
                patches.Add(new FilePatch
                {
                    Id = Guid.NewGuid().ToString(),
                    FilePath = Path.Combine(projectPath ?? "", filePath),
                    PatchType = PatchType.Create,
                    NewContent = content,
                    Description = $"Nouveau fichier: {filePath}",
                    CreatedAt = DateTime.UtcNow
                });
            }
        }

        return patches;
    }

    private FilePatch? ParseDiffToPatch(string diffContent, string? projectPath)
    {
        try
        {
            var lines = diffContent.Split('\n');
            string? filePath = null;

            // Find file path from diff header
            foreach (var line in lines)
            {
                if (line.StartsWith("--- a/") || line.StartsWith("+++ b/"))
                {
                    filePath = line.Substring(6).Trim();
                    break;
                }
            }

            if (string.IsNullOrEmpty(filePath))
                return null;

            return new FilePatch
            {
                Id = Guid.NewGuid().ToString(),
                FilePath = Path.Combine(projectPath ?? "", filePath),
                PatchType = PatchType.Modify,
                UnifiedDiff = diffContent,
                Description = $"Modification: {Path.GetFileName(filePath)}",
                CreatedAt = DateTime.UtcNow
            };
        }
        catch
        {
            return null;
        }
    }

    public override async IAsyncEnumerable<AgentStreamEvent> ProcessStreamAsync(
        string userMessage,
        AgentContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new AgentStreamEvent
        {
            Type = AgentStreamEventType.AgentThinking,
            Content = "Analyse en cours...",
            AgentType = AgentType.Manager
        };

        // For now, use non-streaming and chunk the response
        var response = await ProcessAsync(userMessage, context, cancellationToken);

        // Emit tool calls if any
        if (response.ToolCalls.Any())
        {
            yield return new AgentStreamEvent
            {
                Type = AgentStreamEventType.ToolUse,
                Content = string.Join(", ", response.ToolCalls.Select(t => t.ToolName)),
                AgentType = AgentType.Manager
            };
        }

        // Stream the response in chunks
        var chunkSize = 50;
        for (int i = 0; i < response.Content.Length; i += chunkSize)
        {
            var chunk = response.Content.Substring(i, Math.Min(chunkSize, response.Content.Length - i));
            yield return new AgentStreamEvent
            {
                Type = AgentStreamEventType.TextDelta,
                Content = chunk
            };
            await Task.Delay(5, cancellationToken); // Small delay for visual effect
        }

        // Emit patches if any
        if (response.GeneratedPatches?.Patches.Any() == true)
        {
            yield return new AgentStreamEvent
            {
                Type = AgentStreamEventType.PatchGenerated,
                Content = JsonSerializer.Serialize(response.GeneratedPatches),
                AgentType = AgentType.Manager
            };
        }

        yield return new AgentStreamEvent { Type = AgentStreamEventType.Complete };
    }
}
