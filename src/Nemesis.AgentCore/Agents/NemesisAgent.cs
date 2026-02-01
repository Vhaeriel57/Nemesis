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

    public override string SystemPrompt => @"Tu es Nemesis, expert Unity/C#. RÉPONDS TOUJOURS EN FRANÇAIS.
Quand on te demande de créer/modifier du code: fournis le code complet avec un patch diff.
Utilise les outils pour rechercher dans le projet avant de répondre.";

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
        const int maxTotalLength = 12000; // 16384 tokens - marge pour system prompt et réponse

        // INSTRUCTION CRITIQUE EN PREMIER
        sb.AppendLine("⚠️ RÉPONDS EN FRANÇAIS. Réponds DIRECTEMENT à la question posée.");
        sb.AppendLine();

        // Message utilisateur
        sb.AppendLine("## QUESTION:");
        sb.AppendLine(userMessage);
        sb.AppendLine();

        // Contexte intelligent du projet
        if (_projectKnowledge != null && _projectKnowledge.IsLoaded)
        {
            var keywords = ExtractKeywords(userMessage);

            // 1. Vue d'ensemble rapide des classes
            sb.AppendLine("## 🗺️ CLASSES DU PROJET:");
            var classesOverview = _projectKnowledge.GetClassesOverview();
            sb.AppendLine(TruncateText(classesOverview, 2500));
            sb.AppendLine();

            // 2. Contexte INTELLIGENT - seulement le code pertinent
            var smartContext = _projectKnowledge.GetSmartContext(userMessage, 6000);
            if (!string.IsNullOrEmpty(smartContext))
            {
                sb.AppendLine("## 📄 CODE PERTINENT (classes/méthodes complètes):");
                sb.AppendLine(smartContext);
                sb.AppendLine();
            }

            // 3. Si demande une classe spécifique, on la récupère en entier
            foreach (var keyword in keywords)
            {
                var classInfo = _projectKnowledge.GetClassInfo(keyword);
                if (classInfo != null && classInfo.LineCount <= 200) // Seulement si pas trop grande
                {
                    var classCode = _projectKnowledge.GetClassCode(keyword);
                    if (classCode != null && sb.Length + classCode.Length < maxTotalLength - 1500)
                    {
                        sb.AppendLine($"## 🎯 Classe demandée: {keyword}");
                        sb.AppendLine($"Fichier: {classInfo.FilePath} | Lignes: {classInfo.StartLine}-{classInfo.EndLine}");
                        sb.AppendLine("```csharp");
                        sb.AppendLine(classCode);
                        sb.AppendLine("```");
                        sb.AppendLine();
                    }
                    else if (classCode != null)
                    {
                        // Classe trop grande - montrer juste les méthodes pertinentes
                        sb.AppendLine($"## 🎯 Classe demandée: {keyword} (résumé - {classInfo.LineCount} lignes)");
                        sb.AppendLine($"Fichier: {classInfo.FilePath} | Lignes: {classInfo.StartLine}-{classInfo.EndLine}");
                        sb.AppendLine($"Méthodes: {string.Join(", ", classInfo.Methods.Select(m => $"{m.Name}() L{m.StartLine}"))}");
                        sb.AppendLine();

                        // Méthodes les plus pertinentes
                        foreach (var method in classInfo.Methods.Take(5))
                        {
                            var methodCode = _projectKnowledge.GetMethodCode(keyword, method.Name);
                            if (methodCode != null && sb.Length + methodCode.Length < maxTotalLength - 1000)
                            {
                                sb.AppendLine($"### Méthode: {method.Name}");
                                sb.AppendLine("```csharp");
                                sb.AppendLine(methodCode);
                                sb.AppendLine("```");
                            }
                        }
                        sb.AppendLine();
                    }
                    break; // Une seule classe spécifique
                }
            }
        }

        // Instructions finales
        sb.AppendLine();
        if (IsModificationRequest(userMessage))
        {
            sb.AppendLine("## INSTRUCTION:");
            sb.AppendLine("1. Analyse le code existant ci-dessus");
            sb.AppendLine("2. Crée le code MODIFIÉ complet");
            sb.AppendLine("3. Fournis un PATCH au format diff unifié");
        }
        else if (IsPlanningRequest(userMessage))
        {
            sb.AppendLine("## INSTRUCTION:");
            sb.AppendLine("Explique ton plan détaillé étape par étape:");
            sb.AppendLine("1. Ce que tu as compris de la demande");
            sb.AppendLine("2. Les fichiers/classes existants que tu vas utiliser");
            sb.AppendLine("3. Les modifications ou créations nécessaires");
            sb.AppendLine("4. Comment tout s'interconnecte");
        }

        return sb.ToString();
    }

    private bool IsPlanningRequest(string message)
    {
        var planKeywords = new[] { "que ferais", "comment faire", "explique", "plan", "étapes", "comptes faire" };
        return planKeywords.Any(k => message.Contains(k, StringComparison.OrdinalIgnoreCase));
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
        // Phase 1: Analyse de la demande
        yield return new AgentStreamEvent
        {
            Type = AgentStreamEventType.AgentThinking,
            Content = "💭 Je lis ta question...",
            AgentType = AgentType.Manager
        };

        // Analyse du type de demande
        var requestType = AnalyzeRequestType(userMessage);
        yield return new AgentStreamEvent
        {
            Type = AgentStreamEventType.AgentThinking,
            Content = $"🎯 Type de demande détecté: {requestType}",
            AgentType = AgentType.Manager
        };

        // Phase 2: Extraction des mots-clés
        var keywords = ExtractKeywords(userMessage);
        if (keywords.Any())
        {
            yield return new AgentStreamEvent
            {
                Type = AgentStreamEventType.AgentThinking,
                Content = $"🔑 Mots-clés identifiés: {string.Join(", ", keywords)}",
                AgentType = AgentType.Manager
            };
        }

        // Phase 3: Réflexion sur la stratégie
        yield return new AgentStreamEvent
        {
            Type = AgentStreamEventType.AgentThinking,
            Content = GetStrategyThinking(userMessage, keywords),
            AgentType = AgentType.Manager
        };

        // Phase 4: Recherche intelligente dans le projet
        if (_projectKnowledge != null && _projectKnowledge.IsLoaded)
        {
            yield return new AgentStreamEvent
            {
                Type = AgentStreamEventType.AgentThinking,
                Content = $"📚 Analyse de ma carte du projet ({_projectKnowledge.TotalClasses} classes indexées)...",
                AgentType = AgentType.Manager
            };

            // Recherche intelligente des classes pertinentes
            var foundClasses = new List<string>();
            foreach (var keyword in keywords.Take(3))
            {
                var results = _projectKnowledge.Search(keyword);
                foreach (var result in results.Take(3))
                {
                    var classInfo = _projectKnowledge.GetClassInfo(result.Name);
                    if (classInfo != null)
                    {
                        foundClasses.Add($"{result.Name} (L{classInfo.StartLine}-{classInfo.EndLine})");
                    }
                    else
                    {
                        foundClasses.Add(result.Name);
                    }
                }
            }

            if (foundClasses.Any())
            {
                yield return new AgentStreamEvent
                {
                    Type = AgentStreamEventType.AgentThinking,
                    Content = $"🎯 Classes/méthodes ciblées: {string.Join(", ", foundClasses.Distinct().Take(5))}",
                    AgentType = AgentType.Manager
                };
            }

            // Récupération du contexte intelligent
            yield return new AgentStreamEvent
            {
                Type = AgentStreamEventType.AgentThinking,
                Content = "🧠 Extraction du code pertinent (classes/méthodes complètes, pas de troncature)...",
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
                Content = $"📂 {fileCount} fichier(s) lu(s) pour analyse",
                AgentType = AgentType.Manager
            };
        }

        // Phase 5: Construction du prompt et appel LLM
        yield return new AgentStreamEvent
        {
            Type = AgentStreamEventType.AgentThinking,
            Content = "🧠 Je formule ma réponse...",
            AgentType = AgentType.Manager
        };

        var enrichedPrompt = BuildEnrichedPrompt(userMessage, context, searchResults);
        var messages = BuildMessagesList(context.ChatHistory, enrichedPrompt);
        var toolDefs = Tools.Values.Select(t => t.Definition).ToList();

        var llmResponse = await LlmProvider.CompleteWithToolsAsync(
            messages, toolDefs, SystemPrompt, 0.3, 4096, cancellationToken);

        // Phase 6: Traitement des outils
        var toolCall = ParseToolCall(llmResponse);
        var iterations = 0;
        var maxIterations = 8;

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

            CacheToolResult(toolCall, result);

            // Résumé du résultat
            if (result.Success)
            {
                yield return new AgentStreamEvent
                {
                    Type = AgentStreamEventType.AgentThinking,
                    Content = GetToolResultSummary(toolCall, result),
                    AgentType = AgentType.Manager
                };
            }

            messages.Add(new ChatMessage { Role = "assistant", Content = llmResponse });
            messages.Add(new ChatMessage
            {
                Role = "tool",
                Content = $"Résultat:\n{TruncateText(toolCall.Result ?? "", 500)}"
            });

            llmResponse = await LlmProvider.CompleteWithToolsAsync(
                messages, toolDefs, SystemPrompt, 0.3, 4096, cancellationToken);

            toolCall = ParseToolCall(llmResponse);
        }

        // Phase 7: Réponse finale
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
        var chunkSize = 100;
        for (int i = 0; i < finalContent.Length; i += chunkSize)
        {
            var chunk = finalContent.Substring(i, Math.Min(chunkSize, finalContent.Length - i));
            yield return new AgentStreamEvent
            {
                Type = AgentStreamEventType.TextDelta,
                Content = chunk
            };
            await Task.Delay(2, cancellationToken);
        }

        yield return new AgentStreamEvent { Type = AgentStreamEventType.Complete };
    }

    private string AnalyzeRequestType(string message)
    {
        if (IsPlanningRequest(message))
            return "Demande d'explication/planification";
        if (IsModificationRequest(message))
            return "Demande de création/modification de code";
        if (IsSearchRequest(message))
            return "Recherche d'information";
        return "Question générale";
    }

    private string GetStrategyThinking(string userMessage, List<string> keywords)
    {
        var sb = new StringBuilder();
        sb.Append("💡 Ma stratégie: ");

        if (IsPlanningRequest(userMessage))
        {
            sb.Append("Analyser le contexte, identifier les classes existantes, puis expliquer le plan étape par étape");
        }
        else if (IsModificationRequest(userMessage))
        {
            sb.Append("Trouver les fichiers concernés, comprendre la structure, puis proposer le code");
        }
        else if (keywords.Any())
        {
            sb.Append($"Rechercher '{keywords.First()}' dans le projet et analyser le code trouvé");
        }
        else
        {
            sb.Append("Répondre directement à la question");
        }

        return sb.ToString();
    }

    private string GetToolResultSummary(ToolCall toolCall, ToolResult result)
    {
        if (toolCall.Name == "code_index")
            return $"✓ Recherche terminée - {CountMatches(result.Output ?? "")} résultat(s)";
        if (toolCall.Name == "file_system")
            return $"✓ Fichier lu - {(result.Output?.Length ?? 0)} caractères";
        return $"✓ {toolCall.Name} terminé";
    }

    private int CountMatches(string output)
    {
        return output.Split('\n').Count(l => l.Contains("→") || l.Contains(":"));
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
