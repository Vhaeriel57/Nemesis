using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Nemesis.AgentCore.Services;
using Nemesis.Shared.DTOs;
using Nemesis.Shared.Interfaces;
using Nemesis.Shared.Models;

namespace Nemesis.AgentCore.Agents;

/// <summary>
/// Nemesis - Agent IA intelligent pour le développement Unity
/// Architecture: Connaissance Projet → Recherche → Analyse → Action
/// </summary>
public class NemesisAgent : BaseAgent
{
    private readonly IPatchService? _patchService;
    private readonly ProjectKnowledgeService? _projectKnowledge;

    // Cache de recherche pour la session
    private readonly Dictionary<string, string> _fileCache = new();
    private readonly Dictionary<string, List<CodeSymbolInfo>> _searchCache = new();
    private readonly List<string> _readFiles = new();

    public override string Name => "Nemesis";
    public override AgentType Type => AgentType.Manager;

    public override string SystemPrompt => @"Tu es **Nemesis**, un assistant IA EXPERT en développement Unity.

## RÈGLE ABSOLUE #1: TOUJOURS RECHERCHER AVANT DE RÉPONDRE
Avant CHAQUE réponse sur le code, tu DOIS:
1. Utiliser `code_index` pour chercher les classes/méthodes pertinentes
2. Utiliser `file_system` pour LIRE les fichiers trouvés
3. Analyser le code RÉEL du projet
4. SEULEMENT ENSUITE répondre

## RÈGLE ABSOLUE #2: QUAND L'UTILISATEUR DIT ""C'EST FAUX""
Si l'utilisateur te corrige:
1. Ne répète JAMAIS la même réponse
2. Cherche avec des termes DIFFÉRENTS
3. Lis PLUS de fichiers
4. Admets ton erreur et corrige-toi

## RÈGLE ABSOLUE #3: AGIR, PAS DÉCRIRE
Quand on te demande de MODIFIER du code:
1. NE PAS juste décrire ce que fait le code
2. CRÉER le code modifié COMPLET
3. TOUJOURS fournir un patch au format diff

## Comment utiliser les outils

### Recherche de code (OBLIGATOIRE)
```json
{""tool"": ""code_index"", ""action"": ""search"", ""query"": ""Door""}
```

### Lecture de fichier (OBLIGATOIRE après recherche)
```json
{""tool"": ""file_system"", ""action"": ""read_file"", ""path"": ""Assets/Scripts/Door.cs""}
```

### Recherche web (si besoin de doc Unity)
```json
{""tool"": ""web_search"", ""query"": ""Unity NetworkBehaviour synchronization""}
```

## Format de tes réponses

### Pour les modifications de code
```diff
--- a/Assets/Scripts/TasksHud.cs
+++ b/Assets/Scripts/TasksHud.cs
@@ -15,8 +15,9 @@ public class TasksHud : MonoBehaviour
     text.text =
     ""OBJECTIFS\n"" +
-    $""- Batterie : {(bat ? ""OK"" : ""0/1"")}\n"" +
-    $""- Moteur : {(eng ? ""OK"" : ""0/1"")}\n"" +
+    $""- Huile moteur : {(oil ? ""OK"" : ""0/1"")}\n"" +
```

### Pour les nouveaux fichiers
```csharp
// Fichier: Assets/Scripts/NewFile.cs
using UnityEngine;

public class NewFile : MonoBehaviour
{
    // Code complet
}
```

## Tu réponds TOUJOURS en français, de manière conversationnelle.";

    public override List<string> Capabilities => new()
    {
        "Recherche intelligente dans le code",
        "Lecture et analyse de fichiers",
        "Génération de patches diff",
        "Création de code complet",
        "Recherche web Unity/C#",
        "Raisonnement multi-étapes"
    };

    public NemesisAgent(
        ILlmProvider llmProvider,
        IEnumerable<ITool> tools,
        ILogger<NemesisAgent> logger,
        IPatchService? patchService = null,
        ProjectKnowledgeService? projectKnowledge = null)
        : base(llmProvider, tools, logger)
    {
        _patchService = patchService;
        _projectKnowledge = projectKnowledge;
    }

    public override async Task<AgentResponse> ProcessAsync(
        string userMessage,
        AgentContext context,
        CancellationToken cancellationToken = default)
    {
        var response = new AgentResponse();

        // Phase 1: Extraction des mots-clés et recherche automatique
        var keywords = ExtractKeywords(userMessage);
        var searchResults = await PerformAutomaticSearchAsync(keywords, context, cancellationToken);

        // Phase 2: Construction du prompt enrichi
        var enrichedPrompt = BuildEnrichedPrompt(userMessage, context, searchResults);

        // Build messages list with conversation history
        var messages = BuildMessagesList(context.ChatHistory, enrichedPrompt);

        // Get tool definitions
        var toolDefs = Tools.Values.Select(t => t.Definition).ToList();

        // Phase 3: Appel LLM avec outils
        var llmResponse = await LlmProvider.CompleteWithToolsAsync(
            messages,
            toolDefs,
            SystemPrompt,
            0.2, // Température basse pour plus de précision
            8192,
            cancellationToken);

        // Phase 4: Traitement des appels d'outils en boucle
        var toolCall = ParseToolCall(llmResponse);
        var iterations = 0;
        var maxIterations = 12; // Plus d'itérations pour permettre plus de recherches

        while (toolCall != null && iterations < maxIterations)
        {
            iterations++;
            Logger.LogInformation("Nemesis executing tool: {Tool} (iteration {Iter})", toolCall.Name, iterations);

            var result = await ExecuteToolAsync(toolCall, context, cancellationToken);
            toolCall.Result = result.Success ? result.Output : result.Error;
            toolCall.IsComplete = true;
            response.ToolCalls.Add(toolCall);

            // Cache les résultats
            CacheToolResult(toolCall, result);

            // Continue la conversation avec le résultat
            messages.Add(new ChatMessage { Role = "assistant", Content = llmResponse });
            messages.Add(new ChatMessage
            {
                Role = "tool",
                Content = $"Résultat de {toolCall.Name}:\n```\n{toolCall.Result}\n```\n\nAnalyse ce résultat et continue. Si tu as besoin de plus d'informations, utilise d'autres outils."
            });

            llmResponse = await LlmProvider.CompleteWithToolsAsync(
                messages, toolDefs, SystemPrompt, 0.2, 8192, cancellationToken);

            toolCall = ParseToolCall(llmResponse);
        }

        // Phase 5: Nettoyage et extraction des patches
        response.Content = CleanResponse(llmResponse);

        var patches = ExtractPatches(response.Content, context.ProjectPath);
        if (patches.Any())
        {
            response.GeneratedPatches = new PatchSet
            {
                Description = "Modifications générées par Nemesis",
                Patches = patches
            };

            if (_patchService != null)
            {
                foreach (var patch in patches)
                {
                    _patchService.AddPendingPatch(patch);
                    Logger.LogInformation("Patch créé pour: {FilePath}", patch.FilePath);
                }
            }
        }

        return response;
    }

    /// <summary>
    /// Extrait les mots-clés importants du message utilisateur
    /// </summary>
    private List<string> ExtractKeywords(string message)
    {
        var keywords = new List<string>();

        // Mots en PascalCase ou camelCase (noms de classes/méthodes)
        var codePattern = @"\b([A-Z][a-z]+(?:[A-Z][a-z]+)+|[a-z]+(?:[A-Z][a-z]+)+)\b";
        foreach (Match match in Regex.Matches(message, codePattern))
        {
            keywords.Add(match.Value);
        }

        // Mots entre guillemets
        var quotedPattern = @"""([^""]+)""";
        foreach (Match match in Regex.Matches(message, quotedPattern))
        {
            keywords.Add(match.Groups[1].Value);
        }

        // Mots-clés Unity/C# courants
        var unityKeywords = new[] { "Door", "Player", "Network", "Sync", "RPC", "Command", "Server", "Client",
            "Spawn", "Destroy", "Update", "Start", "Awake", "Controller", "Manager", "Handler", "Service",
            "Task", "Hud", "UI", "Canvas", "Button", "Text", "Input", "Camera", "Audio", "Animation" };

        foreach (var kw in unityKeywords)
        {
            if (message.Contains(kw, StringComparison.OrdinalIgnoreCase))
            {
                keywords.Add(kw);
            }
        }

        // Noms de fichiers mentionnés
        var filePattern = @"(\w+\.cs)\b";
        foreach (Match match in Regex.Matches(message, filePattern))
        {
            keywords.Add(Path.GetFileNameWithoutExtension(match.Groups[1].Value));
        }

        return keywords.Distinct().Take(10).ToList();
    }

    /// <summary>
    /// Effectue des recherches automatiques avant de répondre
    /// </summary>
    private async Task<Dictionary<string, string>> PerformAutomaticSearchAsync(
        List<string> keywords,
        AgentContext context,
        CancellationToken cancellationToken)
    {
        var results = new Dictionary<string, string>();

        if (!keywords.Any() || string.IsNullOrEmpty(context.ProjectPath))
            return results;

        // Recherche dans l'index pour chaque mot-clé
        foreach (var keyword in keywords.Take(5))
        {
            if (_searchCache.ContainsKey(keyword))
                continue;

            try
            {
                var searchTool = Tools.Values.FirstOrDefault(t => t.Definition.Name == "code_index");
                if (searchTool != null)
                {
                    var toolContext = ToToolContext(context);
                    var searchResult = await searchTool.ExecuteAsync(
                        new Dictionary<string, object> { ["action"] = "search", ["query"] = keyword },
                        toolContext,
                        cancellationToken);

                    if (searchResult.Success && !string.IsNullOrEmpty(searchResult.Output))
                    {
                        results[$"search_{keyword}"] = searchResult.Output;

                        // Extraire les chemins de fichiers des résultats et les lire
                        var filePaths = ExtractFilePathsFromSearchResult(searchResult.Output);
                        foreach (var filePath in filePaths.Take(3))
                        {
                            if (!_fileCache.ContainsKey(filePath))
                            {
                                var fileResult = await ReadFileAsync(filePath, context, cancellationToken);
                                if (!string.IsNullOrEmpty(fileResult))
                                {
                                    _fileCache[filePath] = fileResult;
                                    _readFiles.Add(filePath);
                                    results[$"file_{Path.GetFileName(filePath)}"] = fileResult;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Auto-search failed for {Keyword}: {Error}", keyword, ex.Message);
            }
        }

        return results;
    }

    private List<string> ExtractFilePathsFromSearchResult(string searchResult)
    {
        var paths = new List<string>();

        // Pattern pour les chemins de fichiers
        var pathPattern = @"(?:Assets[/\\][^\s\n]+\.cs|[A-Za-z]:[/\\][^\s\n]+\.cs)";
        foreach (Match match in Regex.Matches(searchResult, pathPattern))
        {
            paths.Add(match.Value.Replace("\\", "/"));
        }

        // Pattern pour les noms de fichiers simples
        var filePattern = @"(\w+\.cs)";
        foreach (Match match in Regex.Matches(searchResult, filePattern))
        {
            var fileName = match.Groups[1].Value;
            if (!paths.Any(p => p.EndsWith(fileName)))
            {
                paths.Add(fileName);
            }
        }

        return paths.Distinct().ToList();
    }

    private async Task<string?> ReadFileAsync(string filePath, AgentContext context, CancellationToken cancellationToken)
    {
        try
        {
            var fsTool = Tools.Values.FirstOrDefault(t => t.Definition.Name == "file_system");
            if (fsTool != null)
            {
                var toolContext = ToToolContext(context);

                // Essayer avec le chemin tel quel
                var result = await fsTool.ExecuteAsync(
                    new Dictionary<string, object> { ["action"] = "read_file", ["path"] = filePath },
                    toolContext,
                    cancellationToken);

                if (result.Success)
                    return result.Output;

                // Essayer avec le chemin du projet
                if (!string.IsNullOrEmpty(context.ProjectPath))
                {
                    var fullPath = Path.Combine(context.ProjectPath, filePath);
                    result = await fsTool.ExecuteAsync(
                        new Dictionary<string, object> { ["action"] = "read_file", ["path"] = fullPath },
                        toolContext,
                        cancellationToken);

                    if (result.Success)
                        return result.Output;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning("Failed to read file {Path}: {Error}", filePath, ex.Message);
        }

        return null;
    }

    /// <summary>
    /// Convertit un AgentContext en ToolContext
    /// </summary>
    private static ToolContext ToToolContext(AgentContext context)
    {
        return new ToolContext
        {
            ProjectPath = context.ProjectPath ?? string.Empty,
            CachePath = string.Empty,
            NetworkEnabled = true,
            AllowedPaths = new List<string> { context.ProjectPath ?? string.Empty }
        };
    }

    private string BuildEnrichedPrompt(string userMessage, AgentContext context, Dictionary<string, string> searchResults)
    {
        var sb = new StringBuilder();

        sb.AppendLine("## Message de l'utilisateur");
        sb.AppendLine(userMessage);
        sb.AppendLine();

        // CONNAISSANCE COMPLÈTE DU PROJET (si disponible)
        if (_projectKnowledge != null && _projectKnowledge.IsLoaded)
        {
            sb.AppendLine("## 🗺️ CARTE COMPLÈTE DU PROJET");
            sb.AppendLine(_projectKnowledge.GetProjectMap());
            sb.AppendLine();

            // Recherche intelligente dans la connaissance du projet
            var keywords = ExtractKeywords(userMessage);
            var projectSearchResults = new List<SearchResult>();
            foreach (var keyword in keywords.Take(5))
            {
                projectSearchResults.AddRange(_projectKnowledge.Search(keyword));
            }

            if (projectSearchResults.Any())
            {
                sb.AppendLine("## 🔍 Fichiers/Classes pertinents trouvés dans le projet");
                foreach (var result in projectSearchResults.DistinctBy(r => r.FilePath).Take(15))
                {
                    sb.AppendLine($"- **{result.Name}** ({result.Type}) → `{result.FilePath}`");
                    sb.AppendLine($"  {result.Description}");

                    // Lire le contenu du fichier si trouvé
                    var fileContent = _projectKnowledge.GetFileContent(result.FilePath);
                    if (!string.IsNullOrEmpty(fileContent) && !_fileCache.ContainsKey(result.FilePath))
                    {
                        _fileCache[result.FilePath] = fileContent;
                    }
                }
                sb.AppendLine();

                // Inclure le contenu des fichiers les plus pertinents
                sb.AppendLine("## 📄 Contenu des fichiers pertinents");
                var filesToShow = projectSearchResults
                    .DistinctBy(r => r.FilePath)
                    .Take(5)
                    .ToList();

                foreach (var result in filesToShow)
                {
                    var content = _projectKnowledge.GetFileContent(result.FilePath);
                    if (!string.IsNullOrEmpty(content))
                    {
                        sb.AppendLine($"### `{result.Name}`");
                        sb.AppendLine("```csharp");
                        sb.AppendLine(TruncateText(content, 3000));
                        sb.AppendLine("```");
                        sb.AppendLine();
                    }
                }
            }
        }
        // Contexte du projet (fallback si pas de ProjectKnowledge)
        else if (!string.IsNullOrEmpty(context.ProjectPath))
        {
            sb.AppendLine($"## Projet: `{context.ProjectPath}`");
            sb.AppendLine();
        }

        // Résultats de recherche automatique (outils)
        if (searchResults.Any())
        {
            sb.AppendLine("## Recherches automatiques effectuées");
            sb.AppendLine();

            foreach (var kvp in searchResults)
            {
                if (kvp.Key.StartsWith("search_"))
                {
                    var keyword = kvp.Key.Substring(7);
                    sb.AppendLine($"### Recherche: \"{keyword}\"");
                    sb.AppendLine("```");
                    sb.AppendLine(TruncateText(kvp.Value, 1000));
                    sb.AppendLine("```");
                    sb.AppendLine();
                }
            }

            foreach (var kvp in searchResults)
            {
                if (kvp.Key.StartsWith("file_"))
                {
                    var fileName = kvp.Key.Substring(5);
                    sb.AppendLine($"### Fichier lu: `{fileName}`");
                    sb.AppendLine("```csharp");
                    sb.AppendLine(TruncateText(kvp.Value, 2000));
                    sb.AppendLine("```");
                    sb.AppendLine();
                }
            }
        }

        // Fichiers du contexte
        if (context.RelevantFiles.Any())
        {
            sb.AppendLine("## Fichiers pertinents du contexte");
            foreach (var file in context.RelevantFiles.Take(5))
            {
                sb.AppendLine($"### `{Path.GetFileName(file.Key)}`");
                sb.AppendLine("```csharp");
                sb.AppendLine(TruncateText(file.Value, 1500));
                sb.AppendLine("```");
                sb.AppendLine();
            }
        }

        // Instructions selon le type de demande
        if (IsModificationRequest(userMessage))
        {
            sb.AppendLine("## INSTRUCTION: Modification demandée");
            sb.AppendLine("Tu DOIS:");
            sb.AppendLine("1. Analyser le code existant ci-dessus");
            sb.AppendLine("2. Créer le code MODIFIÉ complet");
            sb.AppendLine("3. Fournir un PATCH au format diff");
            sb.AppendLine("4. NE PAS juste décrire le code");
        }
        else if (IsSearchRequest(userMessage))
        {
            sb.AppendLine("## INSTRUCTION: Recherche demandée");
            sb.AppendLine("Si les résultats ci-dessus ne suffisent pas:");
            sb.AppendLine("1. Utilise `code_index` avec d'autres termes");
            sb.AppendLine("2. Lis les fichiers trouvés avec `file_system`");
            sb.AppendLine("3. Cite TOUJOURS les fichiers que tu as trouvés");
        }

        return sb.ToString();
    }

    private bool IsModificationRequest(string message)
    {
        var modKeywords = new[] { "modifie", "change", "ajoute", "enlève", "supprime", "remplace",
            "update", "fix", "corrige", "rajoute", "retire", "créer", "crée" };
        return modKeywords.Any(k => message.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    private bool IsSearchRequest(string message)
    {
        var searchKeywords = new[] { "trouve", "cherche", "où", "quel", "comment", "pourquoi",
            "montre", "affiche", "liste", "explique" };
        return searchKeywords.Any(k => message.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    private string TruncateText(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            return text;
        return text.Substring(0, maxLength) + "\n// ... (tronqué)";
    }

    private void CacheToolResult(ToolCall toolCall, ToolResult result)
    {
        if (!result.Success) return;

        try
        {
            var args = !string.IsNullOrEmpty(toolCall.Arguments)
                ? JsonSerializer.Deserialize<Dictionary<string, object>>(toolCall.Arguments) ?? new()
                : new Dictionary<string, object>();

            if (toolCall.Name == "code_index")
            {
                var query = args.GetValueOrDefault("query")?.ToString() ?? "";
                if (!string.IsNullOrEmpty(query))
                {
                    // Parse symbols from result if possible
                    _searchCache[query] = new List<CodeSymbolInfo>();
                }
            }
            else if (toolCall.Name == "file_system")
            {
                var path = args.GetValueOrDefault("path")?.ToString() ?? "";
                var action = args.GetValueOrDefault("action")?.ToString() ?? "";
                if (action == "read_file" && !string.IsNullOrEmpty(path) && result.Success)
                {
                    _fileCache[path] = result.Output ?? "";
                    if (!_readFiles.Contains(path))
                        _readFiles.Add(path);
                }
            }
        }
        catch { }
    }

    private List<ChatMessage> BuildMessagesList(List<ChatMessage> history, string currentPrompt)
    {
        var messages = new List<ChatMessage>();

        // Inclure l'historique récent
        foreach (var msg in history.TakeLast(10))
        {
            messages.Add(new ChatMessage { Role = msg.Role, Content = msg.Content });
        }

        messages.Add(new ChatMessage { Role = "user", Content = currentPrompt });

        return messages;
    }

    private string CleanResponse(string response)
    {
        // Nettoyer les appels d'outils JSON résiduels
        var cleaned = Regex.Replace(response, @"\{[\s\S]*?""tool""\s*:\s*""[^""]+""[\s\S]*?\}", "", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"```json\s*```", "", RegexOptions.IgnoreCase);
        return cleaned.Trim();
    }

    private List<FilePatch> ExtractPatches(string response, string? projectPath)
    {
        var patches = new List<FilePatch>();

        // Extraire les blocs diff
        var diffPattern = @"```diff\s*([\s\S]*?)```";
        foreach (Match match in Regex.Matches(response, diffPattern))
        {
            var diffContent = match.Groups[1].Value.Trim();
            var patch = ParseDiffToPatch(diffContent, projectPath);
            if (patch != null)
                patches.Add(patch);
        }

        // Extraire les nouveaux fichiers
        var newFilePattern = @"```csharp\s*//\s*(?:Fichier|File|Path):\s*([^\n]+)\s*([\s\S]*?)```";
        foreach (Match match in Regex.Matches(response, newFilePattern, RegexOptions.IgnoreCase))
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
                    UnifiedDiff = CreateNewFileDiff(filePath, content),
                    CreatedAt = DateTime.UtcNow
                });
            }
        }

        return patches;
    }

    private string CreateNewFileDiff(string filePath, string content)
    {
        var lines = content.Split('\n');
        var diff = new StringBuilder();
        diff.AppendLine($"--- /dev/null");
        diff.AppendLine($"+++ b/{filePath}");
        diff.AppendLine($"@@ -0,0 +1,{lines.Length} @@");
        foreach (var line in lines)
        {
            diff.AppendLine($"+{line}");
        }
        return diff.ToString();
    }

    private FilePatch? ParseDiffToPatch(string diffContent, string? projectPath)
    {
        try
        {
            var lines = diffContent.Split('\n');
            string? filePath = null;

            foreach (var line in lines)
            {
                if (line.StartsWith("+++ b/") || line.StartsWith("+++ "))
                {
                    filePath = line.Replace("+++ b/", "").Replace("+++ ", "").Trim();
                    break;
                }
                if (line.StartsWith("--- a/") || line.StartsWith("--- "))
                {
                    filePath = line.Replace("--- a/", "").Replace("--- ", "").Trim();
                    if (filePath != "/dev/null")
                        break;
                }
            }

            if (string.IsNullOrEmpty(filePath) || filePath == "/dev/null")
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
        // Phase 1: Recherche automatique
        yield return new AgentStreamEvent
        {
            Type = AgentStreamEventType.AgentThinking,
            Content = "🔍 Analyse de ta demande et extraction des mots-clés...",
            AgentType = AgentType.Manager
        };

        var keywords = ExtractKeywords(userMessage);

        if (keywords.Any())
        {
            yield return new AgentStreamEvent
            {
                Type = AgentStreamEventType.AgentThinking,
                Content = $"🔎 Recherche automatique: {string.Join(", ", keywords.Take(5))}...",
                AgentType = AgentType.Manager
            };
        }

        var searchResults = await PerformAutomaticSearchAsync(keywords, context, cancellationToken);

        if (searchResults.Any())
        {
            var fileCount = searchResults.Count(k => k.Key.StartsWith("file_"));
            yield return new AgentStreamEvent
            {
                Type = AgentStreamEventType.AgentThinking,
                Content = $"📂 {fileCount} fichier(s) trouvé(s) et lu(s) automatiquement",
                AgentType = AgentType.Manager
            };
        }

        // Phase 2: Construction du prompt
        yield return new AgentStreamEvent
        {
            Type = AgentStreamEventType.AgentThinking,
            Content = "🧠 Construction du contexte enrichi...",
            AgentType = AgentType.Manager
        };

        var enrichedPrompt = BuildEnrichedPrompt(userMessage, context, searchResults);
        var messages = BuildMessagesList(context.ChatHistory, enrichedPrompt);
        var toolDefs = Tools.Values.Select(t => t.Definition).ToList();

        // Phase 3: Appel LLM
        yield return new AgentStreamEvent
        {
            Type = AgentStreamEventType.AgentThinking,
            Content = "🤔 Réflexion et analyse...",
            AgentType = AgentType.Manager
        };

        var llmResponse = await LlmProvider.CompleteWithToolsAsync(
            messages, toolDefs, SystemPrompt, 0.2, 8192, cancellationToken);

        // Phase 4: Traitement des outils
        var toolCall = ParseToolCall(llmResponse);
        var toolCalls = new List<ToolCall>();
        var iterations = 0;
        var maxIterations = 12;

        while (toolCall != null && iterations < maxIterations)
        {
            iterations++;

            var toolStatus = GetDetailedToolStatus(toolCall);
            yield return new AgentStreamEvent
            {
                Type = AgentStreamEventType.AgentThinking,
                Content = toolStatus,
                AgentType = AgentType.Manager
            };

            var result = await ExecuteToolAsync(toolCall, context, cancellationToken);
            toolCall.Result = result.Success ? result.Output : result.Error;
            toolCall.IsComplete = true;
            toolCalls.Add(toolCall);

            CacheToolResult(toolCall, result);

            yield return new AgentStreamEvent
            {
                Type = AgentStreamEventType.ToolCallComplete,
                Content = toolCall.Name,
                AgentType = AgentType.Manager
            };

            messages.Add(new ChatMessage { Role = "assistant", Content = llmResponse });
            messages.Add(new ChatMessage
            {
                Role = "tool",
                Content = $"Résultat de {toolCall.Name}:\n```\n{toolCall.Result}\n```\n\nAnalyse et continue."
            });

            yield return new AgentStreamEvent
            {
                Type = AgentStreamEventType.AgentThinking,
                Content = "🤔 Analyse des résultats...",
                AgentType = AgentType.Manager
            };

            llmResponse = await LlmProvider.CompleteWithToolsAsync(
                messages, toolDefs, SystemPrompt, 0.2, 8192, cancellationToken);

            toolCall = ParseToolCall(llmResponse);
        }

        // Phase 5: Réponse finale
        var finalContent = CleanResponse(llmResponse);

        // Extraction des patches
        var patches = ExtractPatches(finalContent, context.ProjectPath);
        if (patches.Any() && _patchService != null)
        {
            foreach (var patch in patches)
            {
                _patchService.AddPendingPatch(patch);
            }

            yield return new AgentStreamEvent
            {
                Type = AgentStreamEventType.AgentThinking,
                Content = $"📝 {patches.Count} patch(es) créé(s) → voir l'onglet Patches",
                AgentType = AgentType.Manager
            };
        }

        // Streaming de la réponse
        var chunkSize = 80;
        for (int i = 0; i < finalContent.Length; i += chunkSize)
        {
            var chunk = finalContent.Substring(i, Math.Min(chunkSize, finalContent.Length - i));
            yield return new AgentStreamEvent
            {
                Type = AgentStreamEventType.TextDelta,
                Content = chunk
            };
            await Task.Delay(3, cancellationToken);
        }

        yield return new AgentStreamEvent { Type = AgentStreamEventType.Complete };
    }

    private string GetDetailedToolStatus(ToolCall toolCall)
    {
        var toolName = toolCall.Name?.ToLower() ?? "";

        Dictionary<string, object> args;
        try
        {
            args = !string.IsNullOrEmpty(toolCall.Arguments)
                ? JsonSerializer.Deserialize<Dictionary<string, object>>(toolCall.Arguments) ?? new()
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
            _ => $"🔧 {toolCall.Name}..."
        };
    }

    private string GetFileSystemStatus(Dictionary<string, object> args)
    {
        var action = args.GetValueOrDefault("action")?.ToString() ?? "";
        var path = args.GetValueOrDefault("path")?.ToString() ?? "";
        var fileName = !string.IsNullOrEmpty(path) ? Path.GetFileName(path) : "";

        return action switch
        {
            "read_file" => $"📖 Lecture de {fileName}...",
            "list_directory" => $"📁 Exploration de {fileName}...",
            "search_files" => "🔍 Recherche de fichiers...",
            "write_file" => $"✏️ Écriture de {fileName}...",
            _ => $"📂 {action}..."
        };
    }

    private string GetCodeIndexStatus(Dictionary<string, object> args)
    {
        var action = args.GetValueOrDefault("action")?.ToString() ?? "";
        var query = args.GetValueOrDefault("query")?.ToString() ?? "";

        return action switch
        {
            "search" => $"🔎 Recherche de \"{query}\" dans le code...",
            "find_references" => $"🔗 Recherche des références de {query}...",
            "find_definition" => $"📍 Recherche de la définition de {query}...",
            _ => $"🔎 {action}..."
        };
    }

    private string GetWebSearchStatus(Dictionary<string, object> args)
    {
        var query = args.GetValueOrDefault("query")?.ToString() ?? "";
        var truncated = query.Length > 40 ? query.Substring(0, 40) + "..." : query;
        return $"🌐 Recherche web: \"{truncated}\"...";
    }

    private string GetPatchStatus(Dictionary<string, object> args)
    {
        var action = args.GetValueOrDefault("action")?.ToString() ?? "";
        var filePath = args.GetValueOrDefault("file_path")?.ToString() ?? "";
        var fileName = !string.IsNullOrEmpty(filePath) ? Path.GetFileName(filePath) : "";

        return action switch
        {
            "create" => $"📝 Création du patch pour {fileName}...",
            "apply" => $"✅ Application du patch sur {fileName}...",
            _ => $"📝 {action}..."
        };
    }
}
