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

    public override string SystemPrompt => @"Tu es **Nemesis**, un développeur senior IA expert en Unity. Tu es un vrai collègue dev, pas un robot.

## Qui Tu Es
Expert en **Unity 6/URP**, **C# avancé**, **Netcode for GameObjects**, **Architecture de jeux** (SOLID, patterns).
Tu parles **TOUJOURS en français**, de manière directe et conversationnelle.

## Ton Comportement — Comme un Vrai Collègue
Tu peux et DOIS adapter ton comportement selon la situation :
- **Agir seul** quand tu as assez d'infos pour résoudre le problème
- **Poser des questions** quand tu as besoin de clarifications (""Où se trouve ton script HUD ?"", ""Tu veux ça dans quel Canvas ?"")
- **Proposer de créer de nouveaux fichiers** quand c'est la bonne approche (""Je vais créer FolieBar.cs"")
- **Demander des indications** quand tu ne trouves pas quelque chose (""Je ne trouve pas le script de ton HUD, comment il s'appelle ?"")
- **Expliquer étape par étape** ce que tu proposes, avec du code à chaque étape
- **Donner des options** : ""On peut faire ça de 2 façons : A ou B. Qu'est-ce que tu préfères ?""

## Principes
- Simplicité d'abord. Impact minimal. Pas de fix temporaires.
- Pour les changements non triviaux : ""y a-t-il une manière plus élégante ?""
- Ne dis JAMAIS qu'une tâche est terminée sans le prouver.

## Méthode de Travail

### 1. PLANIFIER
- Formule un plan concret avant de coder
- Explique les étapes à l'utilisateur

### 2. CHERCHER LES BONS FICHIERS — ⚠️ CRITIQUE
**IGNORE les fichiers fournis automatiquement en contexte s'ils ne sont pas pertinents.**
Ils viennent d'une recherche automatique souvent hors-sujet.
**Tu DOIS chercher toi-même** avec `code_index` (search) par mots-clés liés à la demande.
Exemples :
- L'utilisateur parle de ""barre de folie sur le HUD"" → cherche ""HUD"", ""UI"", ""Canvas"", ""Bar"", ""folie"", ""possession""
- L'utilisateur parle d'un bug dans ""NetworkDoor"" → cherche ""NetworkDoor"", ""OnInteract"", ""Door""
- Si tu ne trouves pas → DEMANDE à l'utilisateur : ""Comment s'appelle ton script de HUD ?""

### 3. DIAGNOSTIQUER
- Lis les fichiers que TU as identifiés comme pertinents
- Vérifie les signatures des méthodes avant de proposer un fix
- Ne devine JAMAIS — lis toujours le code source

### 4. CORRIGER — Code COMPLET
- Crée un patch avec `patch` action=""create""
- AFFICHE le code dans ta réponse (blocs ```csharp)
- Si c'est un nouveau fichier, montre le code complet et dis où le créer

## Recherche Web — OBLIGATOIRE
Fais au moins UNE recherche `web_search` par demande.

## ⚠️ PATCHES
- UNIQUEMENT action ""create"" — JAMAIS ""apply""
- L'utilisateur valide dans l'onglet Patches
- TOUJOURS montrer le code DANS la conversation + créer le patch

## Auto-Amélioration
- Retiens les corrections de l'utilisateur pour ne pas refaire les mêmes erreurs";

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

## Instructions

### ÉTAPE 1 : CHERCHE LES BONS FICHIERS TOI-MÊME
⚠️ Les fichiers ci-dessus viennent d'une recherche AUTOMATIQUE — ils peuvent être hors-sujet.
- Utilise `code_index` (search) avec des mots-clés liés à la demande de l'utilisateur
- Utilise `file_system` (read_file) pour lire les fichiers que TU identifies
- Si l'utilisateur mentionne des fichiers spécifiques, lis-les TOUS
- Si tu ne trouves pas un fichier pertinent, DEMANDE à l'utilisateur

### ÉTAPE 2 : COMPRENDRE ET COMMUNIQUER
- Explique ce que tu as trouvé et ton plan d'action
- Si tu as besoin de clarifications, POSE LA QUESTION
- Si tu proposes un nouveau fichier, explique pourquoi et où le mettre

### ÉTAPE 3 : CODER
- Montre le code COMPLET dans ta réponse (blocs ```csharp)
- Crée un patch via `patch` action=""create""
- Après un patch : ""📝 Un patch a été créé, va le vérifier dans l'onglet Patches.""
- ⚠️ JAMAIS action=""apply"" — l'utilisateur valide dans l'onglet Patches

### RAPPELS
- Fais au moins UNE recherche `web_search`
- Vérifie les signatures des méthodes AVANT de proposer du code
- Si tu dis ""je vais lire le fichier"", appelle l'outil IMMÉDIATEMENT";

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
        var maxIterations = 20;
        var allResponsesAccumulator = new StringBuilder();
        allResponsesAccumulator.AppendLine(llmResponse);

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
                Content = $"Résultat de l'outil {toolCall.Name}:\n```\n{toolCall.Result}\n```\n\nContinue : cherche d'autres fichiers pertinents avec `code_index`, lis-les avec `file_system`, ou donne ta réponse si tu as assez d'informations. Si tu as besoin de clarifications, pose la question à l'utilisateur."
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
            allResponsesAccumulator.AppendLine(llmResponse);
        }

        // Clean up any remaining tool call JSON from final response
        response.Content = CleanToolCallsFromResponse(llmResponse);
        if (string.IsNullOrWhiteSpace(response.Content))
        {
            Logger.LogWarning("CleanToolCallsFromResponse stripped entire response — recovering");
            response.Content = CleanToolCallsFromResponse(allResponsesAccumulator.ToString());
            if (string.IsNullOrWhiteSpace(response.Content))
                response.Content = allResponsesAccumulator.ToString();
        }

        // Extract patches from ALL accumulated LLM responses
        var allResponses = allResponsesAccumulator.ToString();
        var patches = ExtractPatchesFromResponse(allResponses, context.ProjectPath);
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

        foreach (var msg in history.TakeLast(20))
        {
            // Working memory from previous exchanges gets injected as a user context message
            if (msg.Role == "system" && msg.Metadata?.ContainsKey("type") == true
                && msg.Metadata["type"]?.ToString() == "working_memory")
            {
                messages.Add(new ChatMessage
                {
                    Role = "user",
                    Content = msg.Content
                });
            }
            else
            {
                messages.Add(new ChatMessage
                {
                    Role = msg.Role,
                    Content = msg.Content
                });
            }
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
        // Remove ```json { "tool": "..." ... } ``` blocks
        var cleaned = Regex.Replace(
            response,
            @"```json\s*\{[^`]*?""tool""\s*:\s*""[^""]+""[^`]*?\}\s*```",
            "",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        // Remove standalone tool call JSON (line starting with { and containing "tool":)
        cleaned = Regex.Replace(
            cleaned,
            @"(?m)^\s*\{[^\n]*""tool""\s*:\s*""[^""]+""[^\}]*\}\s*$",
            "",
            RegexOptions.IgnoreCase);

        // Clean empty json blocks
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

        // Project map — architecture overview (always present if project is loaded)
        if (context.Metadata.TryGetValue("project_map", out var projectMap) && projectMap is string map && !string.IsNullOrEmpty(map))
        {
            sb.AppendLine("### 🗺️ Carte du Projet (architecture)");
            sb.AppendLine(map);
            sb.AppendLine();
        }

        // Smart context — classes/methods most relevant to the user's question
        if (context.Metadata.TryGetValue("smart_context", out var smartCtx) && smartCtx is string smart && !string.IsNullOrEmpty(smart))
        {
            sb.AppendLine("### 🎯 Contexte pertinent pour ta question");
            sb.AppendLine(smart);
            sb.AppendLine();
        }

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

    /// <summary>
    /// Normalizes a combined path to use the correct OS separator (fixes mixed / and \ on Windows).
    /// </summary>
    private static string NormalizePath(string basePath, string relativePath)
    {
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(basePath, normalized));
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

        // 1. Look for ```diff blocks with proper headers
        var diffPattern = @"```diff\s*([\s\S]*?)```";
        var diffMatches = Regex.Matches(response, diffPattern);

        foreach (Match match in diffMatches)
        {
            var diffContent = match.Groups[1].Value.Trim();
            var patch = ParseDiffToPatch(diffContent, projectPath);
            if (patch != null)
            {
                patches.Add(patch);
            }
        }

        // 2. Look for ALL code blocks — match ```csharp, ```cs, ```c#, ```C#, or bare ``` with C#-like content
        var codePattern = @"```(?:csharp|cs|c#)?\s*\r?\n([\s\S]*?)```";
        var codeMatches = Regex.Matches(response, codePattern, RegexOptions.IgnoreCase);

        foreach (Match match in codeMatches)
        {
            var code = match.Groups[1].Value.Trim();
            if (string.IsNullOrEmpty(code) || code.Length < 20) continue;

            // Skip if this was already captured as a diff block
            if (response.Substring(Math.Max(0, match.Index - 5), Math.Min(8, match.Index + 8)).Contains("diff"))
                continue;

            // Only create patches for C#-looking code (has class, void, using, {, etc.)
            if (!LooksLikeCSharp(code)) continue;

            // Try to find a file path mentioned near this code block
            var filePath = FindFilePathNearCodeBlock(response, match.Index, projectPath);

            patches.Add(new FilePatch
            {
                Id = Guid.NewGuid().ToString(),
                FilePath = !string.IsNullOrEmpty(filePath) ? filePath : "code_suggestion.cs",
                Status = PatchStatus.Pending,
                ModifiedContent = code,
                OriginalContent = "",
                UnifiedDiff = GenerateSimpleDiff(filePath ?? "code_suggestion.cs", code),
                CreatedAt = DateTime.UtcNow
            });
        }

        return patches;
    }

    /// <summary>
    /// Checks if a code block looks like C# (to avoid creating patches for JSON, YAML, etc.)
    /// </summary>
    private bool LooksLikeCSharp(string code)
    {
        var indicators = new[] { "class ", "void ", "public ", "private ", "using ", "namespace ", "var ", "return ", "if (", "foreach ", "async ", "=> ", "new ", ".cs" };
        var count = indicators.Count(i => code.Contains(i));
        return count >= 1;
    }

    /// <summary>
    /// Searches the text surrounding a code block for a file path reference.
    /// Looks for patterns like "dans NetworkDoor.cs", "fichier Interactor.cs", "Assets/Scripts/..."
    /// </summary>
    private string? FindFilePathNearCodeBlock(string response, int codeBlockIndex, string? projectPath)
    {
        // Look at the 500 chars before the code block for a file reference
        var searchStart = Math.Max(0, codeBlockIndex - 500);
        var contextBefore = response.Substring(searchStart, codeBlockIndex - searchStart);

        // Pattern 1: explicit path like Assets/Scripts/Foo/Bar.cs
        var pathMatch = Regex.Match(contextBefore, @"(Assets/[^\s\n`""]+\.cs)", RegexOptions.RightToLeft);
        if (pathMatch.Success)
        {
            var relPath = pathMatch.Groups[1].Value;
            return !string.IsNullOrEmpty(projectPath) ? NormalizePath(projectPath, relPath) : relPath;
        }

        // Pattern 2: FileName.cs mentioned (e.g., "dans NetworkDoor.cs", "le fichier Interactor.cs")
        var fileNameMatch = Regex.Match(contextBefore, @"(\w+\.cs)\b", RegexOptions.RightToLeft);
        if (fileNameMatch.Success && !string.IsNullOrEmpty(projectPath))
        {
            var fileName = fileNameMatch.Groups[1].Value;
            // Search for this file in the project
            try
            {
                var found = Directory.GetFiles(projectPath, fileName, SearchOption.AllDirectories).FirstOrDefault();
                if (found != null) return found;
            }
            catch { }
            return fileName;
        }

        return null;
    }

    private string GenerateSimpleDiff(string filePath, string code)
    {
        var fileName = Path.GetFileName(filePath);
        var lines = code.Split('\n');
        var sb = new StringBuilder();
        sb.AppendLine($"--- a/{fileName}");
        sb.AppendLine($"+++ b/{fileName}");
        sb.AppendLine($"@@ -0,0 +1,{lines.Length} @@");
        foreach (var line in lines)
            sb.AppendLine($"+{line}");
        return sb.ToString();
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
                FilePath = NormalizePath(projectPath ?? "", filePath),
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

## Instructions

### ÉTAPE 1 : CHERCHE LES BONS FICHIERS TOI-MÊME
⚠️ Les fichiers ci-dessus viennent d'une recherche AUTOMATIQUE — ils peuvent être hors-sujet.
- Utilise `code_index` (search) avec des mots-clés liés à la demande de l'utilisateur
- Utilise `file_system` (read_file) pour lire les fichiers que TU identifies
- Si l'utilisateur mentionne des fichiers spécifiques, lis-les TOUS
- Si tu ne trouves pas un fichier pertinent, DEMANDE à l'utilisateur

### ÉTAPE 2 : COMPRENDRE ET COMMUNIQUER
- Explique ce que tu as trouvé et ton plan d'action
- Si tu as besoin de clarifications, POSE LA QUESTION
- Si tu proposes un nouveau fichier, explique pourquoi et où le mettre

### ÉTAPE 3 : CODER
- Montre le code COMPLET dans ta réponse (blocs ```csharp)
- Crée un patch via `patch` action=""create""
- Après un patch : ""📝 Un patch a été créé, va le vérifier dans l'onglet Patches.""
- ⚠️ JAMAIS action=""apply"" — l'utilisateur valide dans l'onglet Patches

### RAPPELS
- Fais au moins UNE recherche `web_search`
- Vérifie les signatures des méthodes AVANT de proposer du code
- Si tu dis ""je vais lire le fichier"", appelle l'outil IMMÉDIATEMENT";

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
        var maxIterations = 20;

        // Accumulate ALL LLM responses to extract patches from any of them
        var allResponsesAccumulator = new StringBuilder();
        allResponsesAccumulator.AppendLine(llmResponse);

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
                Content = $"Résultat de l'outil {toolCall.Name}:\n```\n{toolCall.Result}\n```\n\n⚠️ RAPPEL : As-tu lu TOUS les fichiers mentionnés par l'utilisateur ? As-tu vérifié les signatures des méthodes appelées ? Si non, appelle un autre outil MAINTENANT. Ne réponds PAS tant que tu n'as pas tout lu. Raisonne à voix haute."
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
            allResponsesAccumulator.AppendLine(llmResponse);

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

        // Build working memory from all tool calls for continuation support
        var workingMemory = BuildWorkingMemory(toolCalls);
        var hitIterationLimit = toolCall != null && iterations >= maxIterations;

        // Emit working memory so orchestrator can save it in chat history
        if (!string.IsNullOrEmpty(workingMemory))
        {
            yield return new AgentStreamEvent
            {
                Type = AgentStreamEventType.WorkingMemory,
                Content = workingMemory,
                AgentType = AgentType.Manager
            };
        }

        // Clean up final response (for display)
        var finalContent = CleanToolCallsFromResponse(llmResponse);
        Logger.LogInformation("Raw LLM response ({Length} chars): {Response}", llmResponse.Length, llmResponse.Substring(0, Math.Min(500, llmResponse.Length)));
        Logger.LogInformation("After cleanup ({Length} chars): {Content}", finalContent.Length, finalContent.Substring(0, Math.Min(500, finalContent.Length)));

        // If cleanup stripped everything, try to recover from accumulated responses
        if (string.IsNullOrWhiteSpace(finalContent))
        {
            Logger.LogWarning("CleanToolCallsFromResponse stripped entire response — recovering from accumulated text");
            // Use all the thinking text that was extracted during the loop
            var allResponses = allResponsesAccumulator.ToString();
            finalContent = CleanToolCallsFromResponse(allResponses);

            // If still empty, just use the raw response without cleaning
            if (string.IsNullOrWhiteSpace(finalContent))
            {
                Logger.LogWarning("Recovery also empty — using raw accumulated responses");
                finalContent = allResponses;
            }
        }

        // If we hit the iteration limit, append a continuation hint
        if (hitIterationLimit)
        {
            finalContent += "\n\n---\n⚠️ J'ai atteint ma limite d'itérations. Dis **\"continue\"** et je reprendrai exactement où j'en étais, avec tout le contexte de mes recherches.";
            Logger.LogWarning("Hit iteration limit ({Max}). Working memory saved for continuation.", maxIterations);
        }

        // Extract patches from ALL accumulated LLM responses (not just the last one)
        var allResponses = allResponsesAccumulator.ToString();
        Logger.LogInformation("Extracting patches from {Length} chars of accumulated responses", allResponses.Length);
        var patches = ExtractPatchesFromResponse(allResponses, context.ProjectPath);
        Logger.LogInformation("Extracted {Count} patches from response", patches.Count);

        if (patches.Any())
        {
            if (_patchService != null)
            {
                foreach (var patch in patches)
                {
                    _patchService.AddPendingPatch(patch);
                    Logger.LogInformation("Patch added to pending: {FilePath} (ID: {Id})", patch.FilePath, patch.Id);
                }
            }
            else
            {
                Logger.LogWarning("PatchService is null — cannot add patches to pending");
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

    /// <summary>
    /// Builds a compact summary of what the agent discovered during its tool calls.
    /// This gets saved in chat history so "continue" messages have full context.
    /// </summary>
    private string BuildWorkingMemory(List<ToolCall> toolCalls)
    {
        if (!toolCalls.Any()) return "";

        var sb = new StringBuilder();
        sb.AppendLine("[MÉMOIRE DE TRAVAIL — Contexte accumulé par l'agent lors de l'échange précédent]");
        sb.AppendLine();

        foreach (var tc in toolCalls)
        {
            var toolName = tc.Name ?? "unknown";
            var result = tc.Result ?? "";

            // Truncate long results but keep enough context
            if (result.Length > 2000)
                result = result.Substring(0, 2000) + "\n... (tronqué)";

            try
            {
                var args = !string.IsNullOrEmpty(tc.Arguments)
                    ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(tc.Arguments) ?? new()
                    : new();

                switch (toolName.ToLower())
                {
                    case "file_system":
                        var action = args.GetValueOrDefault("action")?.ToString() ?? "";
                        var path = args.GetValueOrDefault("path")?.ToString() ?? args.GetValueOrDefault("file_path")?.ToString() ?? "";
                        if (action == "read_file")
                        {
                            sb.AppendLine($"### Fichier lu : `{Path.GetFileName(path)}`");
                            sb.AppendLine($"Chemin: `{path}`");
                            sb.AppendLine("```csharp");
                            sb.AppendLine(result);
                            sb.AppendLine("```");
                        }
                        else
                        {
                            sb.AppendLine($"### {action} : `{path}`");
                            sb.AppendLine(result);
                        }
                        break;

                    case "code_index":
                        var query = args.GetValueOrDefault("query")?.ToString() ?? "";
                        sb.AppendLine($"### Recherche code : `{query}`");
                        sb.AppendLine(result);
                        break;

                    case "web_search":
                        var searchQuery = args.GetValueOrDefault("query")?.ToString() ?? "";
                        sb.AppendLine($"### Recherche web : `{searchQuery}`");
                        sb.AppendLine(result);
                        break;

                    case "patch":
                        sb.AppendLine($"### Patch créé");
                        var filePath = args.GetValueOrDefault("file_path")?.ToString() ?? "";
                        sb.AppendLine($"Fichier: `{filePath}`");
                        break;

                    default:
                        sb.AppendLine($"### Outil `{toolName}`");
                        sb.AppendLine(result);
                        break;
                }
                sb.AppendLine();
            }
            catch
            {
                sb.AppendLine($"### Outil `{toolName}` — résultat disponible");
                sb.AppendLine();
            }
        }

        sb.AppendLine("[FIN MÉMOIRE DE TRAVAIL]");
        return sb.ToString();
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
            "create" => $"📝 Je prépare un patch pour {fileName} — il sera en attente de ta validation dans l'onglet Patches...",
            "preview" => "👁️ Je vérifie le patch...",
            _ => $"📝 Opération patch ({action})..."
        };
    }
}
