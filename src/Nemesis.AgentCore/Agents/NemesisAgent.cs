using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Nemesis.Shared.DTOs;
using Nemesis.Shared.Interfaces;
using Nemesis.Shared.Models;

namespace Nemesis.AgentCore.Agents;

/// <summary>
/// Nemesis - Agent unifié expert en développement Unity/C#
/// </summary>
public class NemesisAgent : BaseAgent
{
    private readonly IPatchService? _patchService;

    public override string Name => "Nemesis";
    public override AgentType Type => AgentType.Manager;

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
- **Rechercher sur le web** avec `web_search` pour trouver de la documentation ou des solutions
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

## Règles Absolues
1. **TOUJOURS** lire les fichiers pertinents avant de proposer du code
2. **JAMAIS** inventer du code sans connaître le contexte existant
3. **TOUJOURS** proposer du code complet et fonctionnel
4. **TOUJOURS** répondre en français de manière conversationnelle
5. **TOUJOURS** citer les fichiers que tu as consultés
6. **UTILISER** la recherche web quand tu as besoin d'informations récentes ou de documentation";

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
        ILogger<NemesisAgent> logger,
        IPatchService? patchService = null)
        : base(llmProvider, tools, logger)
    {
        _patchService = patchService;
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

        // Get tool definitions from base class Tools dictionary
        var toolDefs = Tools.Values.Select(t => t.Definition).ToList();

        // Call LLM with tools available
        var llmResponse = await LlmProvider.CompleteWithToolsAsync(
            messages,
            toolDefs,
            SystemPrompt,
            0.3,
            8192,
            cancellationToken);

        // Process tool calls using base class method
        var toolCall = ParseToolCall(llmResponse);
        var iterations = 0;
        var maxIterations = 8;

        while (toolCall != null && iterations < maxIterations)
        {
            iterations++;
            Logger.LogInformation("Nemesis executing tool: {Tool}", toolCall.Name);

            var result = await ExecuteToolAsync(toolCall, context, cancellationToken);
            toolCall.Result = result.Success ? result.Output : result.Error;
            toolCall.IsComplete = true;
            response.ToolCalls.Add(toolCall);

            // Add tool result to messages and continue
            messages.Add(new ChatMessage
            {
                Role = "assistant",
                Content = llmResponse
            });
            messages.Add(new ChatMessage
            {
                Role = "tool",
                Content = $"Résultat de l'outil {toolCall.Name}:\n```\n{toolCall.Result}\n```\n\nContinue ta réponse."
            });

            // Get next response
            llmResponse = await LlmProvider.CompleteWithToolsAsync(
                messages,
                toolDefs,
                SystemPrompt,
                0.3,
                8192,
                cancellationToken);

            toolCall = ParseToolCall(llmResponse);
        }

        // Clean up any remaining tool call JSON from final response
        response.Content = CleanToolCallsFromResponse(llmResponse);

        // Extract any patches from the response
        var patches = ExtractPatchesFromResponse(response.Content, context.ProjectPath);
        if (patches.Any())
        {
            response.GeneratedPatches = new PatchSet
            {
                Description = "Patches générés par Nemesis",
                Patches = patches
            };

            // Add patches to PatchService if available
            if (_patchService != null)
            {
                foreach (var patch in patches)
                {
                    _patchService.AddPendingPatch(patch);
                    Logger.LogInformation("Patch added to pending: {FilePath}", patch.FilePath);
                }
            }
        }

        return response;
    }

    private List<ChatMessage> BuildMessagesList(List<ChatMessage> history, string currentPrompt)
    {
        var messages = new List<ChatMessage>();

        foreach (var msg in history.TakeLast(10))
        {
            messages.Add(new ChatMessage
            {
                Role = msg.Role,
                Content = msg.Content
            });
        }

        messages.Add(new ChatMessage
        {
            Role = "user",
            Content = currentPrompt
        });

        return messages;
    }

    private string CleanToolCallsFromResponse(string response)
    {
        var cleaned = Regex.Replace(
            response,
            @"\{[\s\S]*?""tool""\s*:\s*""[^""]+""[\s\S]*?\}",
            "",
            RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"```json\s*```", "", RegexOptions.IgnoreCase);
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

        if (context.RelevantFiles.Any())
        {
            sb.AppendLine("### Fichiers pertinents identifiés:");
            foreach (var kvp in context.RelevantFiles.Take(8))
            {
                var filePath = kvp.Key;
                var content = kvp.Value;
                var relativePath = GetRelativePath(filePath, context.ProjectPath);
                var preview = content.Length > 1500 ? content.Substring(0, 1500) + "\n// ... (tronqué)" : content;

                sb.AppendLine($"\n#### `{relativePath}`");
                sb.AppendLine("```csharp");
                sb.AppendLine(preview);
                sb.AppendLine("```");
            }
        }

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

        // Also look for new file blocks
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
                    Status = PatchStatus.Pending,
                    ModifiedContent = content,
                    OriginalContent = "",
                    UnifiedDiff = $"--- /dev/null\n+++ b/{filePath}\n@@ -0,0 +1,{content.Split('\n').Length} @@\n" +
                                  string.Join("\n", content.Split('\n').Select(l => $"+{l}")),
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
                Status = PatchStatus.Pending,
                UnifiedDiff = diffContent,
                OriginalContent = "",
                ModifiedContent = "",
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
            Content = "🔍 Analyse de ta demande...",
            AgentType = AgentType.Manager
        };

        // Build comprehensive project context
        var projectContext = BuildEnhancedProjectContext(context);

        yield return new AgentStreamEvent
        {
            Type = AgentStreamEventType.AgentThinking,
            Content = "📚 Construction du contexte projet...",
            AgentType = AgentType.Manager
        };

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

        // Get tool definitions from base class Tools dictionary
        var toolDefs = Tools.Values.Select(t => t.Definition).ToList();

        yield return new AgentStreamEvent
        {
            Type = AgentStreamEventType.AgentThinking,
            Content = "🤔 Réflexion en cours...",
            AgentType = AgentType.Manager
        };

        // Call LLM with tools available
        var llmResponse = await LlmProvider.CompleteWithToolsAsync(
            messages,
            toolDefs,
            SystemPrompt,
            0.3,
            8192,
            cancellationToken);

        // Process tool calls
        var toolCall = ParseToolCall(llmResponse);
        var toolCalls = new List<ToolCall>();
        var iterations = 0;
        var maxIterations = 8;

        while (toolCall != null && iterations < maxIterations)
        {
            iterations++;

            // Emit detailed status for each tool
            var toolStatus = GetDetailedToolStatus(toolCall);
            yield return new AgentStreamEvent
            {
                Type = AgentStreamEventType.AgentThinking,
                Content = toolStatus,
                AgentType = AgentType.Manager
            };

            Logger.LogInformation("Nemesis executing tool: {Tool}", toolCall.Name);

            var result = await ExecuteToolAsync(toolCall, context, cancellationToken);
            toolCall.Result = result.Success ? result.Output : result.Error;
            toolCall.IsComplete = true;
            toolCalls.Add(toolCall);

            // Emit tool completion
            yield return new AgentStreamEvent
            {
                Type = AgentStreamEventType.ToolCallComplete,
                Content = toolCall.Name,
                AgentType = AgentType.Manager
            };

            // Add tool result to messages and continue
            messages.Add(new ChatMessage
            {
                Role = "assistant",
                Content = llmResponse
            });
            messages.Add(new ChatMessage
            {
                Role = "tool",
                Content = $"Résultat de l'outil {toolCall.Name}:\n```\n{toolCall.Result}\n```\n\nContinue ta réponse."
            });

            yield return new AgentStreamEvent
            {
                Type = AgentStreamEventType.AgentThinking,
                Content = "🤔 Analyse des résultats...",
                AgentType = AgentType.Manager
            };

            // Get next response
            llmResponse = await LlmProvider.CompleteWithToolsAsync(
                messages,
                toolDefs,
                SystemPrompt,
                0.3,
                8192,
                cancellationToken);

            toolCall = ParseToolCall(llmResponse);
        }

        // Clean up final response
        var finalContent = CleanToolCallsFromResponse(llmResponse);

        // Extract patches
        var patches = ExtractPatchesFromResponse(finalContent, context.ProjectPath);
        if (patches.Any() && _patchService != null)
        {
            foreach (var patch in patches)
            {
                _patchService.AddPendingPatch(patch);
                Logger.LogInformation("Patch added to pending: {FilePath}", patch.FilePath);
            }

            yield return new AgentStreamEvent
            {
                Type = AgentStreamEventType.AgentThinking,
                Content = $"📝 {patches.Count} patch(es) créé(s) - voir l'onglet Patches",
                AgentType = AgentType.Manager
            };
        }

        // Stream the response in chunks
        var chunkSize = 50;
        for (int i = 0; i < finalContent.Length; i += chunkSize)
        {
            var chunk = finalContent.Substring(i, Math.Min(chunkSize, finalContent.Length - i));
            yield return new AgentStreamEvent
            {
                Type = AgentStreamEventType.TextDelta,
                Content = chunk
            };
            await Task.Delay(5, cancellationToken);
        }

        yield return new AgentStreamEvent { Type = AgentStreamEventType.Complete };
    }

    private string GetDetailedToolStatus(ToolCall toolCall)
    {
        var toolName = toolCall.Name?.ToLower() ?? "";

        // Parse Arguments JSON string into Dictionary
        Dictionary<string, object> args;
        try
        {
            args = !string.IsNullOrEmpty(toolCall.Arguments)
                ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(toolCall.Arguments)
                  ?? new Dictionary<string, object>()
                : new Dictionary<string, object>();
        }
        catch
        {
            args = new Dictionary<string, object>();
        }

        return toolName switch
        {
            "file_system" => GetFileSystemStatus(args),
            "code_index" => GetCodeIndexStatus(args),
            "web_search" => GetWebSearchStatus(args),
            "patch" => GetPatchStatus(args),
            _ => $"🔧 Utilisation de {toolCall.Name}..."
        };
    }

    private string GetFileSystemStatus(Dictionary<string, object> args)
    {
        var action = args.GetValueOrDefault("action")?.ToString() ?? "";
        var path = args.GetValueOrDefault("path")?.ToString() ?? args.GetValueOrDefault("file_path")?.ToString() ?? "";
        var fileName = !string.IsNullOrEmpty(path) ? Path.GetFileName(path) : "";

        return action switch
        {
            "read_file" => $"📂 Lecture de {fileName}...",
            "list_directory" => $"📁 Liste du dossier {fileName}...",
            "search_files" => "🔎 Recherche de fichiers...",
            _ => $"📂 Opération fichier: {action}..."
        };
    }

    private string GetCodeIndexStatus(Dictionary<string, object> args)
    {
        var action = args.GetValueOrDefault("action")?.ToString() ?? "";
        var query = args.GetValueOrDefault("query")?.ToString() ?? "";

        return action switch
        {
            "search" => $"🔎 Recherche de '{query}' dans le code...",
            "find_references" => $"🔗 Recherche des références de {query}...",
            "find_definition" => $"📍 Recherche de la définition de {query}...",
            _ => $"🔎 Recherche dans l'index: {action}..."
        };
    }

    private string GetWebSearchStatus(Dictionary<string, object> args)
    {
        var query = args.GetValueOrDefault("query")?.ToString() ?? "";
        var truncatedQuery = query.Length > 30 ? query.Substring(0, 30) + "..." : query;
        return $"🌐 Recherche web: \"{truncatedQuery}\"...";
    }

    private string GetPatchStatus(Dictionary<string, object> args)
    {
        var action = args.GetValueOrDefault("action")?.ToString() ?? "";
        var filePath = args.GetValueOrDefault("file_path")?.ToString() ?? "";
        var fileName = !string.IsNullOrEmpty(filePath) ? Path.GetFileName(filePath) : "";

        return action switch
        {
            "create" => $"📝 Création d'un patch pour {fileName}...",
            "preview" => "👁️ Prévisualisation du patch...",
            "apply" => $"✅ Application du patch sur {fileName}...",
            _ => $"📝 Opération patch: {action}..."
        };
    }
}
