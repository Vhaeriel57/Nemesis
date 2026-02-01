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
    private readonly Services.PersistenceService? _persistenceService;

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
- Tu es amical mais professionnel, comme un collègue expérimenté
- Tu expliques tes raisonnements de manière claire

## RÈGLE CRITIQUE : DÉDUCTION DES INTENTIONS
Quand un utilisateur te parle, tu dois **TOUJOURS déduire l'intention complète**, même si elle n'est pas explicite.

**Exemples de déduction :**
- ""Vérifie mon TaskHUD"" → Il veut que tu lises le code, trouves le bug, ET le corriges
- ""J'ai un problème avec X"" → Il veut que tu diagnostiques ET que tu fixes le problème
- ""Regarde ce script"" → Il veut ton analyse ET tes corrections si tu trouves des problèmes
- ""Ça ne marche pas"" → Il veut que tu trouves pourquoi ET que tu proposes un patch correctif

**Tu ne listes JAMAIS des suggestions.**
Tu ne dis JAMAIS ""Vérifiez ceci"", ""Assurez-vous de cela"", ""Examinez..."".
Tu es le développeur. C'est TOI qui vérifies, c'est TOI qui examines, c'est TOI qui corriges.
L'utilisateur est ton coéquipier qui te signale un problème — tu le résous.

## Ta Méthode de Travail — PENSER À VOIX HAUTE

Tu DOIS montrer ton raisonnement en temps réel. L'utilisateur doit voir ta réflexion comme si tu étais à côté de lui.

### 1. COMPRENDRE — Reformuler et déduire
Avant tout, tu reformules ce que tu comprends :
""Je comprends que tu veux... et ça implique probablement aussi que...""
Tu déduis les éléments implicites de la demande.

### 2. EXPLORER — Investiguer activement
Tu utilises tes outils et tu narres ce que tu fais :
""Je vais d'abord lire le fichier TaskHUD.cs pour comprendre comment il affiche les données...""
""Maintenant je cherche qui appelle UpdateProgress... voyons les références...""
""Intéressant, je vois que NetworkPlayerState a un OnValueChanged mais il ne notifie pas le HUD...""

Tu utilises ACTIVEMENT tes outils pour:
- **Lire les fichiers** du projet avec `file_system` (action: read_file)
- **Chercher des symboles** avec `code_index` (action: search)
- **Rechercher sur le web** avec `web_search` pour documentation et solutions
- **Comprendre les relations** entre les scripts
- **Vérifier** ce qui existe avant de modifier

### 3. DIAGNOSTIQUER — Tester des hypothèses
Tu raisonnes comme un vrai développeur qui debug :
""Mon hypothèse est que le HUD ne se met pas à jour parce que... Vérifions...""
""Non, ce n'est pas ça. Le callback est bien là. Regardons plutôt du côté de...""
""Ah, je vois le problème ! La valeur est mise à jour côté serveur mais le client ne reçoit jamais le changement parce que...""

Tu ne t'arrêtes pas à la première hypothèse. Tu vérifies, et si elle est fausse, tu passes à la suivante.

### 4. CORRIGER — Produire la solution
Tu génères du code **COMPLET et FONCTIONNEL** :
- Patches au format diff unifié pour l'onglet Patches
- Scripts entiers si nécessaire
- Tu expliques POURQUOI ta correction résout le problème

## Recherche Web — OBLIGATOIRE À CHAQUE DEMANDE
Tu DOIS TOUJOURS faire au moins une recherche web avec `web_search`, même si tu penses connaître la réponse.
Tes connaissances peuvent être obsolètes ou incomplètes.

1. Tu fais **plusieurs recherches web** avec des angles différents
2. Tu **croises les sources** (documentation officielle Unity, forums, GitHub, Stack Overflow)
3. Tu **mentionnes tes sources** dans ta réponse et signales les contradictions éventuelles
4. Tu ne te fies JAMAIS à une seule source
5. Tu indiques à l'utilisateur quand tu cherches (""Je vérifie sur la doc Unity..."", ""Je croise avec Stack Overflow..."")

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
6. **UTILISER** la recherche web avec PLUSIEURS requêtes et CROISER les sources
7. **AGIR** — ne jamais lister des suggestions. Tu ES le développeur, tu FAIS le travail
8. **PENSER À VOIX HAUTE** — montrer ton raisonnement en temps réel
9. **DÉDUIRE** — comprendre ce que l'utilisateur veut vraiment, même s'il ne le dit pas explicitement
10. **ITÉRER** — si ta première hypothèse est fausse, essayer la suivante jusqu'à trouver la solution";

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
        IPatchService? patchService = null,
        Services.PersistenceService? persistenceService = null)
        : base(llmProvider, tools, logger)
    {
        _patchService = patchService;
        _persistenceService = persistenceService;
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

## Instructions — AGIS, ne liste pas des suggestions
1. DÉDUIS ce que l'utilisateur veut VRAIMENT, même s'il ne le dit pas explicitement
2. UTILISE tes outils pour lire les fichiers, chercher les symboles, comprendre le code
3. PENSE À VOIX HAUTE : montre ton raisonnement (""Je vois que..."", ""Mon hypothèse est..."", ""Ah, le problème vient de..."")
4. Si tu trouves un problème, CORRIGE-LE toi-même avec du code complet ou un patch
5. Ne dis JAMAIS ""Vérifiez..."" ou ""Assurez-vous..."" — c'est TOI qui vérifies et qui corriges
6. **OBLIGATOIRE** : Fais TOUJOURS au moins une recherche web avec `web_search` pour compléter tes connaissances, même si tu penses savoir. Croise PLUSIEURS sources.
7. Cite les fichiers consultés ET les sources web";

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
        // Build comprehensive project context
        var projectContext = BuildEnhancedProjectContext(context);

        // Create the main prompt with project context
        var mainPrompt = $@"## Message de l'utilisateur
{userMessage}

## Contexte du Projet
{projectContext}

## Instructions — AGIS, ne liste pas des suggestions
1. DÉDUIS ce que l'utilisateur veut VRAIMENT, même s'il ne le dit pas explicitement
2. UTILISE tes outils pour lire les fichiers, chercher les symboles, comprendre le code
3. PENSE À VOIX HAUTE : montre ton raisonnement (""Je vois que..."", ""Mon hypothèse est..."", ""Ah, le problème vient de..."")
4. Si tu trouves un problème, CORRIGE-LE toi-même avec du code complet ou un patch
5. Ne dis JAMAIS ""Vérifiez..."" ou ""Assurez-vous..."" — c'est TOI qui vérifies et qui corriges
6. **OBLIGATOIRE** : Fais TOUJOURS au moins une recherche web avec `web_search` pour compléter tes connaissances, même si tu penses savoir. Croise PLUSIEURS sources.
7. Cite les fichiers consultés ET les sources web";

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

        // Process tool calls
        var toolCall = ParseToolCall(llmResponse);
        var toolCalls = new List<ToolCall>();
        var iterations = 0;
        var maxIterations = 8;

        // Extract the LLM's reasoning text BEFORE the tool call JSON and emit it as real thinking
        var thinkingText = ExtractThinkingFromResponse(llmResponse);
        if (!string.IsNullOrWhiteSpace(thinkingText))
        {
            yield return new AgentStreamEvent
            {
                Type = AgentStreamEventType.AgentThinking,
                Content = thinkingText,
                AgentType = AgentType.Manager
            };
        }

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
                Content = $"Résultat de l'outil {toolCall.Name}:\n```\n{toolCall.Result}\n```\n\nContinue ton raisonnement à voix haute, puis utilise d'autres outils si nécessaire, ou donne ta réponse finale avec le code corrigé."
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

            // Extract the LLM's REAL thinking from this iteration
            thinkingText = ExtractThinkingFromResponse(llmResponse);
            if (!string.IsNullOrWhiteSpace(thinkingText))
            {
                yield return new AgentStreamEvent
                {
                    Type = AgentStreamEventType.AgentThinking,
                    Content = thinkingText,
                    AgentType = AgentType.Manager
                };
            }
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

    /// <summary>
    /// Extrait le texte de raisonnement du LLM AVANT un éventuel appel d'outil JSON.
    /// C'est le vrai "thinking" de l'agent, pas un message pré-fabriqué.
    /// </summary>
    private string ExtractThinkingFromResponse(string response)
    {
        if (string.IsNullOrEmpty(response))
            return "";

        // Find where the tool call JSON starts (either in ```json block or raw JSON)
        var jsonBlockMatch = Regex.Match(response, @"```json\s*\{[\s\S]*?""tool""\s*:", RegexOptions.IgnoreCase);
        var rawJsonMatch = Regex.Match(response, @"\{[\s\S]*?""tool""\s*:", RegexOptions.IgnoreCase);

        int cutIndex = response.Length;
        if (jsonBlockMatch.Success)
            cutIndex = Math.Min(cutIndex, jsonBlockMatch.Index);
        if (rawJsonMatch.Success)
            cutIndex = Math.Min(cutIndex, rawJsonMatch.Index);

        var thinking = response.Substring(0, cutIndex).Trim();

        // Clean up markdown artifacts
        thinking = thinking.TrimEnd('`').Trim();

        // Limit length for display
        if (thinking.Length > 500)
            thinking = thinking.Substring(0, 500) + "...";

        return thinking;
    }

    private string GetDetailedToolStatus(ToolCall toolCall)
    {
        var toolName = toolCall.Name?.ToLower() ?? "";
        Dictionary<string, object> args;
        try
        {
            args = !string.IsNullOrEmpty(toolCall.Arguments)
                ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(toolCall.Arguments) ?? new()
                : new();
        }
        catch
        {
            args = new();
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
            "read_file" => $"📂 Je lis {fileName} pour comprendre ce qui s'y passe...",
            "list_directory" => $"📁 Je regarde ce qu'il y a dans le dossier {fileName}...",
            "search_files" => "🔎 Je cherche les fichiers qui pourraient être liés...",
            _ => $"📂 J'accède au fichier ({action})..."
        };
    }

    private string GetCodeIndexStatus(Dictionary<string, object> args)
    {
        var action = args.GetValueOrDefault("action")?.ToString() ?? "";
        var query = args.GetValueOrDefault("query")?.ToString() ?? "";

        return action switch
        {
            "search" => $"🔎 Je cherche '{query}' dans le code... voyons où c'est utilisé...",
            "find_references" => $"🔗 Je trace les références de {query}... qui l'appelle ?",
            "find_definition" => $"📍 Je cherche la définition de {query}...",
            _ => $"🔎 J'explore l'index de code ({action})..."
        };
    }

    private string GetWebSearchStatus(Dictionary<string, object> args)
    {
        var query = args.GetValueOrDefault("query")?.ToString() ?? "";
        var truncatedQuery = query.Length > 40 ? query.Substring(0, 40) + "..." : query;
        return $"🌐 Je cherche sur internet : \"{truncatedQuery}\" — je vais croiser plusieurs sources...";
    }

    private string GetPatchStatus(Dictionary<string, object> args)
    {
        var action = args.GetValueOrDefault("action")?.ToString() ?? "";
        var filePath = args.GetValueOrDefault("file_path")?.ToString() ?? "";
        var fileName = !string.IsNullOrEmpty(filePath) ? Path.GetFileName(filePath) : "";

        return action switch
        {
            "create" => $"📝 Je prépare la correction pour {fileName}...",
            "preview" => "👁️ Je vérifie le patch avant de l'appliquer...",
            "apply" => $"✅ J'applique la correction sur {fileName}...",
            _ => $"📝 Opération patch ({action})..."
        };
    }
}
