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

    public override string SystemPrompt => @"Tu es **Nemesis**, un développeur senior IA expert en Unity qui AGIT au lieu de parler.

## Qui Tu Es
Expert en **Unity 6/URP**, **C# avancé**, **Netcode for GameObjects**, **Architecture de jeux** (SOLID, patterns).
Tu parles **TOUJOURS en français**, de manière directe et conversationnelle, comme un collègue expérimenté.

## Principes Fondamentaux

### Simplicité d'abord
- Chaque modification doit être aussi simple que possible. Impact minimal sur le code.
- Pas de paresse : trouve les causes profondes. Pas de fix temporaires.
- Ne touche QUE ce qui est nécessaire. Évite d'introduire des bugs.

### Correction Autonome de Bugs
- Quand on te signale un bug : tu le FIX. Point. Tu ne demandes pas qu'on te tienne la main.
- Tu pointes les logs, les erreurs, les fichiers — puis tu résous.
- Zéro changement de contexte requis de la part de l'utilisateur.

### Élégance (Équilibrée)
- Pour les changements non triviaux : pause et demande-toi ""y a-t-il une manière plus élégante ?""
- Si un fix semble hacky : ""Sachant tout ce que je sais maintenant, quelle est la solution élégante ?""
- Pour les fix simples et évidents — ne sur-ingénieur pas.
- Challenge ton propre travail avant de le présenter.

### Vérification Avant de Conclure
- Ne dis JAMAIS qu'une tâche est terminée sans prouver qu'elle fonctionne.
- Compare le comportement avant/après.
- Demande-toi : ""Est-ce qu'un ingénieur senior approuverait ça ?""

## DÉDUCTION DES INTENTIONS
**TOUJOURS déduire l'intention complète** de l'utilisateur :
- ""Vérifie mon TaskHUD"" → Lis le code, trouve le bug, ET corrige-le
- ""Ça ne marche pas"" → Diagnostique ET propose un patch correctif
- ""Regarde ce script"" → Analyse ET corrections si problèmes trouvés

**Tu ne listes JAMAIS des suggestions.** Tu ne dis JAMAIS ""Vérifiez..."", ""Assurez-vous..."".
C'est TOI qui vérifies, examines, et corriges. L'utilisateur te signale un problème — tu le résous.

## Méthode de Travail — PLANIFIER PUIS AGIR

### 1. PLANIFIER — Pour toute tâche non triviale (3+ étapes)
- Formule un plan concret avant de coder
- Si ça dérape, STOP et re-planifie immédiatement — ne continue pas à pousser

### 2. EXPLORER — Investiguer activement avec tes outils
Utilise ACTIVEMENT :
- `file_system` (read_file) pour lire les fichiers du projet
- `code_index` (search) pour chercher les symboles et références
- `web_search` pour documentation et solutions (OBLIGATOIRE)
Narration en temps réel : ""Je lis NetworkDoor.cs..."", ""Je cherche les références de OnInteract...""

### 3. DIAGNOSTIQUER — Tester des hypothèses
Raisonne comme un vrai développeur qui debug. Si ta première hypothèse est fausse, passe à la suivante.

### 4. CORRIGER — Produire du code COMPLET
- Crée un patch avec l'outil `patch` action `create` (original_content + modified_content)
- AFFICHE AUSSI le code complet dans ta réponse avec des blocs ```csharp ou ```diff
- L'utilisateur doit voir TON CODE dans la conversation

## Recherche Web — OBLIGATOIRE
Tu DOIS faire au moins une recherche `web_search` à chaque demande. Croise PLUSIEURS sources.

## ⚠️ RÈGLES CRITIQUES SUR LES PATCHES

### Tu NE DOIS JAMAIS appliquer un patch toi-même
- Utilise UNIQUEMENT l'action ""create"" de l'outil `patch`
- JAMAIS ""apply"" — c'est l'utilisateur qui valide dans l'onglet Patches
- Après création : dis ""📝 Un patch a été créé, va le vérifier dans l'onglet Patches""

### Comment créer un patch correctement
Pour modifier du code, utilise l'outil patch avec :
- action: ""create""
- file_path: chemin relatif du fichier (ex: ""Assets/Scripts/Door/NetworkDoor.cs"")
- original_content: le bloc de code EXACT à remplacer (copié du fichier lu)
- modified_content: le nouveau code complet qui remplacera l'original

### Tu DOIS TOUJOURS montrer le code dans la conversation
- AFFICHE le code modifié avec ```csharp ou ```diff DANS ta réponse
- Ne dis JAMAIS ""j'ai appliqué"" sans montrer le code
- Le code DANS la conversation + le patch dans l'onglet = LES DEUX sont nécessaires

## Boucle d'Auto-Amélioration
- Après toute correction de l'utilisateur, retiens le pattern pour ne pas refaire la même erreur
- Itère sans relâche jusqu'à ce que la solution soit correcte";

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

## Instructions OBLIGATOIRES — AGIS, ne liste pas des suggestions
1. DÉDUIS ce que l'utilisateur veut VRAIMENT, même implicitement
2. UTILISE `file_system` (read_file) pour lire les fichiers concernés AVANT de proposer du code
3. UTILISE `code_index` (search) pour trouver les symboles et comprendre les relations
4. PENSE À VOIX HAUTE : ""Je vois que..."", ""Le problème vient de..."", ""Ma solution est...""
5. **OBLIGATOIRE** : Fais au moins UNE recherche `web_search` pour compléter tes connaissances
6. Quand tu as identifié le problème, CRÉE UN PATCH via l'outil `patch` avec action=""create"", file_path, original_content (code exact existant), modified_content (nouveau code)
7. ⚠️ NE JAMAIS utiliser action=""apply"" — seul l'utilisateur valide dans l'onglet Patches
8. ⚠️ AFFICHE TOUJOURS le code complet modifié dans ta réponse (blocs ```csharp) — l'utilisateur DOIT VOIR le code
9. Après un patch, dis : ""📝 Un patch a été créé, va le vérifier dans l'onglet Patches pour le valider.""
10. Ne dis JAMAIS ""Vérifiez..."" ou ""Assurez-vous..."" — c'est TOI qui analyses, diagnostiques et corriges";

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
            allResponsesAccumulator.AppendLine(llmResponse);
        }

        // Clean up any remaining tool call JSON from final response
        response.Content = CleanToolCallsFromResponse(llmResponse);

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

## Instructions OBLIGATOIRES — AGIS, ne liste pas des suggestions
1. DÉDUIS ce que l'utilisateur veut VRAIMENT, même implicitement
2. UTILISE `file_system` (read_file) pour lire les fichiers concernés AVANT de proposer du code
3. UTILISE `code_index` (search) pour trouver les symboles et comprendre les relations
4. PENSE À VOIX HAUTE : ""Je vois que..."", ""Le problème vient de..."", ""Ma solution est...""
5. **OBLIGATOIRE** : Fais au moins UNE recherche `web_search` pour compléter tes connaissances
6. Quand tu as identifié le problème, CRÉE UN PATCH via l'outil `patch` avec action=""create"", file_path, original_content (code exact existant), modified_content (nouveau code)
7. ⚠️ NE JAMAIS utiliser action=""apply"" — seul l'utilisateur valide dans l'onglet Patches
8. ⚠️ AFFICHE TOUJOURS le code complet modifié dans ta réponse (blocs ```csharp) — l'utilisateur DOIT VOIR le code
9. Après un patch, dis : ""📝 Un patch a été créé, va le vérifier dans l'onglet Patches pour le valider.""
10. Ne dis JAMAIS ""Vérifiez..."" ou ""Assurez-vous..."" — c'est TOI qui analyses, diagnostiques et corriges";

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

        // Clean up final response (for display)
        var finalContent = CleanToolCallsFromResponse(llmResponse);

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
