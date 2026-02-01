using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Nemesis.CodeAnalysis.Indexing;
using Nemesis.Shared.Models;

namespace Nemesis.AgentCore.Services;

/// <summary>
/// Service qui maintient une connaissance COMPLÈTE du projet Unity.
/// Charge et indexe TOUS les fichiers pour que l'agent comprenne le projet entier.
/// </summary>
public class ProjectKnowledgeService
{
    private readonly CodeIndexer _codeIndexer;
    private readonly ILogger<ProjectKnowledgeService> _logger;

    // Connaissance complète du projet
    private string? _projectPath;
    private readonly Dictionary<string, ProjectFile> _allFiles = new();
    private readonly Dictionary<string, ClassInfo> _allClasses = new();
    private readonly Dictionary<string, List<string>> _classRelations = new(); // class -> classes it references
    private readonly Dictionary<string, List<string>> _filesByFolder = new();
    private readonly Dictionary<string, FileMap> _fileMaps = new(); // Cartes détaillées de chaque fichier
    private string _projectSummary = "";
    private bool _isLoaded = false;

    public bool IsLoaded => _isLoaded;
    public string ProjectPath => _projectPath ?? "";
    public int TotalFiles => _allFiles.Count;
    public int TotalClasses => _allClasses.Count;

    public ProjectKnowledgeService(CodeIndexer codeIndexer, ILogger<ProjectKnowledgeService> logger)
    {
        _codeIndexer = codeIndexer;
        _logger = logger;
    }

    /// <summary>
    /// Charge et analyse TOUT le projet Unity
    /// </summary>
    public async Task LoadProjectAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        _projectPath = projectPath;
        _allFiles.Clear();
        _allClasses.Clear();
        _classRelations.Clear();
        _filesByFolder.Clear();
        _fileMaps.Clear();

        _logger.LogInformation("Chargement complet du projet: {Path}", projectPath);

        // Trouver tous les fichiers C#
        var scriptsPath = Path.Combine(projectPath, "Assets");
        if (!Directory.Exists(scriptsPath))
        {
            scriptsPath = projectPath;
        }

        var csFiles = Directory.GetFiles(scriptsPath, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("Library") && !f.Contains("Temp") && !f.Contains(".git"))
            .ToList();

        _logger.LogInformation("Trouvé {Count} fichiers C# à analyser", csFiles.Count);

        // Analyser chaque fichier
        foreach (var filePath in csFiles)
        {
            if (cancellationToken.IsCancellationRequested) break;

            try
            {
                await LoadFileAsync(filePath, projectPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Erreur lors de l'analyse de {File}: {Error}", filePath, ex.Message);
            }
        }

        // Construire les relations entre classes
        BuildClassRelations();

        // Générer le résumé du projet
        _projectSummary = GenerateProjectSummary();

        _isLoaded = true;
        _logger.LogInformation("Projet chargé: {Files} fichiers, {Classes} classes", _allFiles.Count, _allClasses.Count);
    }

    private async Task LoadFileAsync(string filePath, string projectPath)
    {
        var content = await File.ReadAllTextAsync(filePath);
        var lines = content.Split('\n');
        var relativePath = GetRelativePath(filePath, projectPath);
        var folder = Path.GetDirectoryName(relativePath) ?? "";

        var projectFile = new ProjectFile
        {
            FullPath = filePath,
            RelativePath = relativePath,
            FileName = Path.GetFileName(filePath),
            Content = content,
            Folder = folder
        };

        // Créer la carte du fichier avec les numéros de ligne
        var fileMap = BuildFileMap(filePath, relativePath, lines);
        _fileMaps[relativePath] = fileMap;

        // Extraire les informations de classe (mise à jour avec lignes)
        ExtractClassInfoWithLines(projectFile, lines, fileMap);

        _allFiles[relativePath] = projectFile;

        // Organiser par dossier
        if (!_filesByFolder.ContainsKey(folder))
            _filesByFolder[folder] = new List<string>();
        _filesByFolder[folder].Add(relativePath);
    }

    /// <summary>
    /// Construit une carte détaillée du fichier avec tous les chunks et leurs lignes
    /// </summary>
    private FileMap BuildFileMap(string fullPath, string relativePath, string[] lines)
    {
        var fileMap = new FileMap
        {
            FilePath = relativePath,
            FileName = Path.GetFileName(fullPath),
            TotalLines = lines.Length
        };

        // Extraire les usings
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith("using ") && line.EndsWith(";"))
            {
                var usingNs = line.Substring(6, line.Length - 7).Trim();
                fileMap.Usings.Add(usingNs);
            }
            else if (line.StartsWith("namespace "))
            {
                var ns = ExtractNamespace(line);
                if (!string.IsNullOrEmpty(ns))
                    fileMap.Namespaces.Add(ns);
            }
        }

        // Parser les types (classes, structs, interfaces, enums) avec leurs membres
        ParseTypesWithLineNumbers(lines, relativePath, fileMap);

        return fileMap;
    }

    private string ExtractNamespace(string line)
    {
        var match = Regex.Match(line, @"namespace\s+([\w.]+)");
        return match.Success ? match.Groups[1].Value : "";
    }

    /// <summary>
    /// Parse les types et leurs membres avec les numéros de ligne exacts
    /// </summary>
    private void ParseTypesWithLineNumbers(string[] lines, string filePath, FileMap fileMap)
    {
        var typePattern = @"^\s*(?:public|private|protected|internal)?\s*(?:partial\s+)?(?:abstract\s+)?(?:sealed\s+)?(?:static\s+)?(class|struct|interface|enum)\s+(\w+)";

        int braceDepth = 0;
        CodeChunk? currentType = null;
        int typeStartBrace = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var lineNumber = i + 1; // Les lignes commencent à 1

            // Chercher un nouveau type
            var typeMatch = Regex.Match(line, typePattern);
            if (typeMatch.Success && currentType == null)
            {
                var kind = typeMatch.Groups[1].Value;
                var name = typeMatch.Groups[2].Value;

                currentType = new CodeChunk
                {
                    Name = name,
                    Type = kind switch
                    {
                        "class" => CodeChunkType.Class,
                        "struct" => CodeChunkType.Struct,
                        "interface" => CodeChunkType.Interface,
                        "enum" => CodeChunkType.Enum,
                        _ => CodeChunkType.Class
                    },
                    FilePath = filePath,
                    StartLine = lineNumber,
                    Signature = line.Trim()
                };

                // Compter les accolades pour trouver la fin
                braceDepth = 0;
                typeStartBrace = -1;
            }

            // Compter les accolades
            foreach (char c in line)
            {
                if (c == '{')
                {
                    if (currentType != null && typeStartBrace == -1)
                    {
                        typeStartBrace = braceDepth;
                    }
                    braceDepth++;
                }
                else if (c == '}')
                {
                    braceDepth--;
                    if (currentType != null && braceDepth == typeStartBrace)
                    {
                        // Fin du type actuel
                        currentType.EndLine = lineNumber;
                        currentType.Content = string.Join("\n", lines.Skip(currentType.StartLine - 1).Take(currentType.LineCount));

                        // Parser les méthodes dans ce type
                        ParseMethodsInType(lines, currentType);

                        fileMap.Chunks.Add(currentType);
                        currentType = null;
                        typeStartBrace = -1;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Parse les méthodes à l'intérieur d'un type
    /// </summary>
    private void ParseMethodsInType(string[] lines, CodeChunk typeChunk)
    {
        var methodPattern = @"^\s*(?:\[[^\]]+\]\s*)*(?:public|private|protected|internal)?\s*(?:static\s+)?(?:virtual\s+)?(?:override\s+)?(?:async\s+)?([\w<>\[\],\?\s]+)\s+(\w+)\s*\(([^)]*)\)";

        int braceDepth = 0;
        CodeChunk? currentMethod = null;
        int methodStartBrace = -1;
        bool inType = false;

        for (int i = typeChunk.StartLine - 1; i < typeChunk.EndLine && i < lines.Length; i++)
        {
            var line = lines[i];
            var lineNumber = i + 1;

            // Ignorer les lignes avant l'accolade ouvrante du type
            if (!inType)
            {
                if (line.Contains("{"))
                    inType = true;
                continue;
            }

            // Chercher une méthode (seulement si on n'est pas déjà dans une méthode)
            if (currentMethod == null)
            {
                var methodMatch = Regex.Match(line, methodPattern);
                if (methodMatch.Success)
                {
                    var returnType = methodMatch.Groups[1].Value.Trim();
                    var methodName = methodMatch.Groups[2].Value;
                    var parameters = methodMatch.Groups[3].Value;

                    // Ignorer les mots-clés et constructeurs
                    if (methodName != "if" && methodName != "while" && methodName != "for" &&
                        methodName != "switch" && methodName != "catch" && methodName != "using" &&
                        methodName != typeChunk.Name) // pas le constructeur
                    {
                        currentMethod = new CodeChunk
                        {
                            Name = methodName,
                            Type = CodeChunkType.Method,
                            FilePath = typeChunk.FilePath,
                            StartLine = lineNumber,
                            Signature = $"{returnType} {methodName}({parameters})",
                            ParentName = typeChunk.Name
                        };
                        methodStartBrace = -1;
                    }
                }
            }

            // Compter les accolades pour les méthodes
            if (currentMethod != null)
            {
                foreach (char c in line)
                {
                    if (c == '{')
                    {
                        if (methodStartBrace == -1)
                            methodStartBrace = braceDepth;
                        braceDepth++;
                    }
                    else if (c == '}')
                    {
                        braceDepth--;
                        if (braceDepth == methodStartBrace)
                        {
                            currentMethod.EndLine = lineNumber;
                            currentMethod.Content = string.Join("\n", lines.Skip(currentMethod.StartLine - 1).Take(currentMethod.LineCount));
                            typeChunk.Children.Add(currentMethod);
                            currentMethod = null;
                            methodStartBrace = -1;
                        }
                    }
                }
            }
            else
            {
                // Juste compter les accolades
                foreach (char c in line)
                {
                    if (c == '{') braceDepth++;
                    else if (c == '}') braceDepth--;
                }
            }
        }
    }

    /// <summary>
    /// Version mise à jour de ExtractClassInfo qui utilise les données de FileMap
    /// </summary>
    private void ExtractClassInfoWithLines(ProjectFile file, string[] lines, FileMap fileMap)
    {
        foreach (var chunk in fileMap.Chunks)
        {
            if (chunk.Type == CodeChunkType.Class || chunk.Type == CodeChunkType.Struct ||
                chunk.Type == CodeChunkType.Interface || chunk.Type == CodeChunkType.Enum)
            {
                var classInfo = new ClassInfo
                {
                    Name = chunk.Name,
                    Kind = chunk.Type.ToString().ToLower(),
                    FilePath = file.RelativePath,
                    StartLine = chunk.StartLine,
                    EndLine = chunk.EndLine,
                    BaseClasses = ExtractBaseClasses(chunk.Signature),
                    Methods = chunk.Children
                        .Where(c => c.Type == CodeChunkType.Method)
                        .Select(c => new MethodInfo
                        {
                            Name = c.Name,
                            Signature = c.Signature,
                            StartLine = c.StartLine,
                            EndLine = c.EndLine,
                            IsUnityCallback = IsUnityCallback(c.Name)
                        }).ToList()
                };

                // Détecter MonoBehaviour et ScriptableObject
                classInfo.IsMonoBehaviour = classInfo.BaseClasses.Any(b =>
                    b.Contains("MonoBehaviour") || b.Contains("NetworkBehaviour"));
                classInfo.IsScriptableObject = classInfo.BaseClasses.Any(b =>
                    b.Contains("ScriptableObject"));

                _allClasses[chunk.Name] = classInfo;
                file.Classes.Add(classInfo);
            }
        }

        // Extraire les usings
        file.Usings = fileMap.Usings;
    }

    private List<string> ExtractBaseClasses(string signature)
    {
        var match = Regex.Match(signature, @":\s*([^{]+)");
        if (!match.Success)
            return new List<string>();

        return match.Groups[1].Value.Split(',')
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList();
    }

    private bool IsUnityCallback(string methodName)
    {
        var unityCallbacks = new[] { "Awake", "Start", "Update", "FixedUpdate", "LateUpdate",
            "OnEnable", "OnDisable", "OnDestroy", "OnTriggerEnter", "OnTriggerExit",
            "OnCollisionEnter", "OnCollisionExit", "OnGUI", "OnDrawGizmos" };
        return unityCallbacks.Contains(methodName);
    }

    private void BuildClassRelations()
    {
        foreach (var classInfo in _allClasses.Values)
        {
            var relations = new List<string>();

            // Ajouter les classes de base
            relations.AddRange(classInfo.BaseClasses.Where(b => _allClasses.ContainsKey(b)));

            // Chercher les références dans le fichier
            var file = _allFiles.Values.FirstOrDefault(f => f.RelativePath == classInfo.FilePath);
            if (file != null)
            {
                foreach (var otherClass in _allClasses.Keys)
                {
                    if (otherClass != classInfo.Name && file.Content.Contains(otherClass))
                    {
                        relations.Add(otherClass);
                    }
                }
            }

            _classRelations[classInfo.Name] = relations.Distinct().ToList();
        }
    }

    private string GenerateProjectSummary()
    {
        var sb = new StringBuilder();

        sb.AppendLine("# RÉSUMÉ COMPLET DU PROJET");
        sb.AppendLine();

        // Stats générales
        sb.AppendLine($"## Statistiques");
        sb.AppendLine($"- **Fichiers C#**: {_allFiles.Count}");
        sb.AppendLine($"- **Classes/Structs/Enums**: {_allClasses.Count}");
        sb.AppendLine($"- **MonoBehaviours**: {_allClasses.Values.Count(c => c.IsMonoBehaviour)}");
        sb.AppendLine($"- **ScriptableObjects**: {_allClasses.Values.Count(c => c.IsScriptableObject)}");
        sb.AppendLine();

        // Structure par dossier
        sb.AppendLine("## Structure du projet");
        foreach (var folder in _filesByFolder.OrderBy(f => f.Key))
        {
            var folderName = string.IsNullOrEmpty(folder.Key) ? "(racine)" : folder.Key;
            sb.AppendLine($"\n### 📁 {folderName}");
            foreach (var file in folder.Value.Take(20))
            {
                var fileName = Path.GetFileName(file);
                var projectFile = _allFiles[file];
                var classNames = string.Join(", ", projectFile.Classes.Select(c => c.Name));
                sb.AppendLine($"- `{fileName}` → {(string.IsNullOrEmpty(classNames) ? "(pas de classe)" : classNames)}");
            }
            if (folder.Value.Count > 20)
            {
                sb.AppendLine($"  ... et {folder.Value.Count - 20} autres fichiers");
            }
        }
        sb.AppendLine();

        // Classes importantes (MonoBehaviours et NetworkBehaviours)
        sb.AppendLine("## Classes principales");

        var networkClasses = _allClasses.Values
            .Where(c => c.BaseClasses.Any(b => b.Contains("Network")))
            .ToList();

        if (networkClasses.Any())
        {
            sb.AppendLine("\n### 🌐 Classes réseau (NetworkBehaviour)");
            foreach (var cls in networkClasses.Take(30))
            {
                sb.AppendLine($"- **{cls.Name}** (`{cls.FilePath}`)");
                if (cls.Methods.Any(m => m.Name.StartsWith("Cmd") || m.Name.StartsWith("Rpc")))
                {
                    var rpcs = cls.Methods.Where(m => m.Name.StartsWith("Cmd") || m.Name.StartsWith("Rpc")).Select(m => m.Name);
                    sb.AppendLine($"  - RPCs: {string.Join(", ", rpcs)}");
                }
            }
        }

        var monoBehaviours = _allClasses.Values
            .Where(c => c.IsMonoBehaviour && !c.BaseClasses.Any(b => b.Contains("Network")))
            .ToList();

        if (monoBehaviours.Any())
        {
            sb.AppendLine("\n### 🎮 MonoBehaviours");
            foreach (var cls in monoBehaviours.Take(30))
            {
                sb.AppendLine($"- **{cls.Name}** (`{cls.FilePath}`)");
            }
        }

        // Managers et Controllers
        var managers = _allClasses.Values
            .Where(c => c.Name.Contains("Manager") || c.Name.Contains("Controller") || c.Name.Contains("Handler"))
            .ToList();

        if (managers.Any())
        {
            sb.AppendLine("\n### 🎛️ Managers & Controllers");
            foreach (var cls in managers.Take(20))
            {
                sb.AppendLine($"- **{cls.Name}** (`{cls.FilePath}`)");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Retourne la carte complète du projet pour l'agent
    /// </summary>
    public string GetProjectMap()
    {
        return _projectSummary;
    }

    /// <summary>
    /// Retourne le contenu d'un fichier
    /// </summary>
    public string? GetFileContent(string fileNameOrPath)
    {
        // Chercher par nom de fichier
        var file = _allFiles.Values.FirstOrDefault(f =>
            f.FileName.Equals(fileNameOrPath, StringComparison.OrdinalIgnoreCase) ||
            f.RelativePath.EndsWith(fileNameOrPath, StringComparison.OrdinalIgnoreCase));

        return file?.Content;
    }

    /// <summary>
    /// Recherche des fichiers/classes par mot-clé
    /// </summary>
    public List<SearchResult> Search(string query)
    {
        var results = new List<SearchResult>();
        var queryLower = query.ToLower();

        // Chercher dans les noms de classes
        foreach (var cls in _allClasses.Values.Where(c => c.Name.ToLower().Contains(queryLower)))
        {
            results.Add(new SearchResult
            {
                Type = "class",
                Name = cls.Name,
                FilePath = cls.FilePath,
                Description = $"{cls.Kind} {cls.Name} : {string.Join(", ", cls.BaseClasses)}"
            });
        }

        // Chercher dans les noms de fichiers
        foreach (var file in _allFiles.Values.Where(f => f.FileName.ToLower().Contains(queryLower)))
        {
            if (!results.Any(r => r.FilePath == file.RelativePath))
            {
                results.Add(new SearchResult
                {
                    Type = "file",
                    Name = file.FileName,
                    FilePath = file.RelativePath,
                    Description = $"Fichier contenant: {string.Join(", ", file.Classes.Select(c => c.Name))}"
                });
            }
        }

        // Chercher dans le contenu
        foreach (var file in _allFiles.Values)
        {
            if (file.Content.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                if (!results.Any(r => r.FilePath == file.RelativePath))
                {
                    results.Add(new SearchResult
                    {
                        Type = "content",
                        Name = file.FileName,
                        FilePath = file.RelativePath,
                        Description = $"Contient '{query}'"
                    });
                }
            }
        }

        return results.Take(50).ToList();
    }

    /// <summary>
    /// Retourne toutes les classes qui utilisent/référencent une classe donnée
    /// </summary>
    public List<string> GetClassReferences(string className)
    {
        return _classRelations
            .Where(r => r.Value.Contains(className))
            .Select(r => r.Key)
            .ToList();
    }

    /// <summary>
    /// Retourne les infos d'une classe
    /// </summary>
    public ClassInfo? GetClassInfo(string className)
    {
        return _allClasses.GetValueOrDefault(className);
    }

    /// <summary>
    /// Retourne tous les fichiers du projet
    /// </summary>
    public IEnumerable<ProjectFile> GetAllFiles() => _allFiles.Values;

    #region Smart Code Retrieval Methods

    /// <summary>
    /// Retourne la carte d'un fichier spécifique
    /// </summary>
    public FileMap? GetFileMap(string fileNameOrPath)
    {
        var key = _fileMaps.Keys.FirstOrDefault(k =>
            k.EndsWith(fileNameOrPath, StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(k).Equals(fileNameOrPath, StringComparison.OrdinalIgnoreCase));

        return key != null ? _fileMaps[key] : null;
    }

    /// <summary>
    /// Retourne UNIQUEMENT le code d'une classe spécifique (sans le reste du fichier)
    /// </summary>
    public string? GetClassCode(string className)
    {
        if (!_allClasses.TryGetValue(className, out var classInfo))
            return null;

        var file = _allFiles.Values.FirstOrDefault(f => f.RelativePath == classInfo.FilePath);
        if (file == null)
            return null;

        var lines = file.Content.Split('\n');
        if (classInfo.StartLine <= 0 || classInfo.EndLine <= 0 ||
            classInfo.StartLine > lines.Length || classInfo.EndLine > lines.Length)
            return null;

        var classLines = lines.Skip(classInfo.StartLine - 1).Take(classInfo.LineCount);
        return $"// Fichier: {classInfo.FilePath}\n// Lignes {classInfo.StartLine}-{classInfo.EndLine}\n\n" +
               string.Join("\n", classLines);
    }

    /// <summary>
    /// Retourne UNIQUEMENT le code d'une méthode spécifique
    /// </summary>
    public string? GetMethodCode(string className, string methodName)
    {
        if (!_allClasses.TryGetValue(className, out var classInfo))
            return null;

        var method = classInfo.Methods.FirstOrDefault(m =>
            m.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase));
        if (method == null)
            return null;

        var file = _allFiles.Values.FirstOrDefault(f => f.RelativePath == classInfo.FilePath);
        if (file == null)
            return null;

        var lines = file.Content.Split('\n');
        if (method.StartLine <= 0 || method.EndLine <= 0 ||
            method.StartLine > lines.Length || method.EndLine > lines.Length)
            return null;

        var methodLines = lines.Skip(method.StartLine - 1).Take(method.EndLine - method.StartLine + 1);
        return $"// Fichier: {classInfo.FilePath}\n// Classe: {className}\n// Méthode: {methodName} (Lignes {method.StartLine}-{method.EndLine})\n\n" +
               string.Join("\n", methodLines);
    }

    /// <summary>
    /// Retourne un contexte intelligent basé sur une requête
    /// Trouve les classes et méthodes les plus pertinentes
    /// </summary>
    public string GetSmartContext(string query, int maxChars = 8000)
    {
        var sb = new StringBuilder();
        var queryLower = query.ToLower();
        var keywords = queryLower.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2)
            .ToList();

        // Score les classes par pertinence
        var scoredClasses = _allClasses.Values
            .Select(cls => new
            {
                Class = cls,
                Score = CalculateRelevanceScore(cls, keywords, queryLower)
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Take(5)
            .ToList();

        sb.AppendLine("# CONTEXTE PERTINENT POUR LA REQUÊTE");
        sb.AppendLine();

        foreach (var item in scoredClasses)
        {
            var cls = item.Class;
            var classCode = GetClassCode(cls.Name);

            if (classCode != null && sb.Length + classCode.Length < maxChars)
            {
                sb.AppendLine($"## {cls.Kind} {cls.Name} (score: {item.Score})");
                sb.AppendLine($"Fichier: {cls.FilePath} | Lignes: {cls.StartLine}-{cls.EndLine}");
                sb.AppendLine($"Hérite de: {string.Join(", ", cls.BaseClasses)}");
                sb.AppendLine($"Méthodes: {string.Join(", ", cls.Methods.Select(m => m.Name))}");
                sb.AppendLine();
                sb.AppendLine("```csharp");
                sb.AppendLine(classCode);
                sb.AppendLine("```");
                sb.AppendLine();
            }
            else if (classCode != null)
            {
                // Si la classe est trop grande, on prend juste les méthodes pertinentes
                sb.AppendLine($"## {cls.Kind} {cls.Name} (score: {item.Score}) [RÉSUMÉ - classe trop grande]");
                sb.AppendLine($"Fichier: {cls.FilePath} | Lignes: {cls.StartLine}-{cls.EndLine} ({cls.LineCount} lignes)");
                sb.AppendLine($"Hérite de: {string.Join(", ", cls.BaseClasses)}");
                sb.AppendLine();

                // Ajouter les méthodes les plus pertinentes
                var relevantMethods = cls.Methods
                    .Where(m => keywords.Any(k => m.Name.ToLower().Contains(k)))
                    .Take(3);

                foreach (var method in relevantMethods)
                {
                    var methodCode = GetMethodCode(cls.Name, method.Name);
                    if (methodCode != null && sb.Length + methodCode.Length < maxChars)
                    {
                        sb.AppendLine($"### Méthode: {method.Name}");
                        sb.AppendLine("```csharp");
                        sb.AppendLine(methodCode);
                        sb.AppendLine("```");
                        sb.AppendLine();
                    }
                }
            }

            if (sb.Length >= maxChars)
                break;
        }

        return sb.ToString();
    }

    /// <summary>
    /// Calcule un score de pertinence pour une classe basé sur des mots-clés
    /// </summary>
    private int CalculateRelevanceScore(ClassInfo cls, List<string> keywords, string fullQuery)
    {
        int score = 0;
        var classNameLower = cls.Name.ToLower();

        // Score basé sur le nom de la classe
        foreach (var keyword in keywords)
        {
            if (classNameLower.Contains(keyword))
                score += 10;
            if (classNameLower == keyword)
                score += 20;
        }

        // Score basé sur les noms de méthodes
        foreach (var method in cls.Methods)
        {
            var methodNameLower = method.Name.ToLower();
            foreach (var keyword in keywords)
            {
                if (methodNameLower.Contains(keyword))
                    score += 5;
                if (methodNameLower == keyword)
                    score += 10;
            }
        }

        // Score basé sur les classes de base
        foreach (var baseClass in cls.BaseClasses)
        {
            var baseClassLower = baseClass.ToLower();
            foreach (var keyword in keywords)
            {
                if (baseClassLower.Contains(keyword))
                    score += 3;
            }
        }

        // Bonus pour les types importants
        if (cls.IsMonoBehaviour) score += 2;
        if (cls.Name.Contains("Manager") || cls.Name.Contains("Controller")) score += 2;

        return score;
    }

    /// <summary>
    /// Retourne une liste de toutes les classes avec leurs signatures (pour la carte mentale de l'agent)
    /// </summary>
    public string GetClassesOverview()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# CARTE DES CLASSES DU PROJET");
        sb.AppendLine();

        foreach (var cls in _allClasses.Values.OrderBy(c => c.FilePath).ThenBy(c => c.Name))
        {
            sb.AppendLine($"- **{cls.Name}** ({cls.Kind}) @ `{cls.FilePath}` L{cls.StartLine}-{cls.EndLine}");
            if (cls.BaseClasses.Any())
            {
                sb.AppendLine($"  Hérite: {string.Join(", ", cls.BaseClasses)}");
            }
            if (cls.Methods.Any())
            {
                var methodsList = cls.Methods.Take(10).Select(m => $"{m.Name}()");
                sb.Append($"  Méthodes: {string.Join(", ", methodsList)}");
                if (cls.Methods.Count > 10)
                    sb.Append($" ... +{cls.Methods.Count - 10}");
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Retourne les lignes spécifiques d'un fichier (utile pour lire une portion précise)
    /// </summary>
    public string? GetFileLines(string fileNameOrPath, int startLine, int endLine)
    {
        var file = _allFiles.Values.FirstOrDefault(f =>
            f.FileName.Equals(fileNameOrPath, StringComparison.OrdinalIgnoreCase) ||
            f.RelativePath.EndsWith(fileNameOrPath, StringComparison.OrdinalIgnoreCase));

        if (file == null)
            return null;

        var lines = file.Content.Split('\n');
        if (startLine < 1) startLine = 1;
        if (endLine > lines.Length) endLine = lines.Length;

        var selectedLines = lines.Skip(startLine - 1).Take(endLine - startLine + 1);
        return $"// Fichier: {file.RelativePath}\n// Lignes {startLine}-{endLine}\n\n" +
               string.Join("\n", selectedLines);
    }

    #endregion

    private string GetRelativePath(string fullPath, string basePath)
    {
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
}

// Classes de données
public class ProjectFile
{
    public string FullPath { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Folder { get; set; } = "";
    public string Content { get; set; } = "";
    public List<ClassInfo> Classes { get; set; } = new();
    public List<string> Usings { get; set; } = new();
}

public class ClassInfo
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "class";
    public string FilePath { get; set; } = "";
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public int LineCount => EndLine - StartLine + 1;
    public List<string> BaseClasses { get; set; } = new();
    public List<MethodInfo> Methods { get; set; } = new();
    public List<string> Properties { get; set; } = new();
    public List<string> Fields { get; set; } = new();
    public bool IsMonoBehaviour { get; set; }
    public bool IsScriptableObject { get; set; }
}

public class MethodInfo
{
    public string Name { get; set; } = "";
    public string Parameters { get; set; } = "";
    public string ReturnType { get; set; } = "";
    public bool IsUnityCallback { get; set; }
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public string Signature { get; set; } = "";
}

public class SearchResult
{
    public string Type { get; set; } = "";
    public string Name { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string Description { get; set; } = "";
}

/// <summary>
/// Représente un "chunk" de code avec ses limites de lignes
/// </summary>
public class CodeChunk
{
    public string Name { get; set; } = "";
    public CodeChunkType Type { get; set; }
    public string FilePath { get; set; } = "";
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public int LineCount => EndLine - StartLine + 1;
    public string Content { get; set; } = "";
    public string Signature { get; set; } = ""; // Signature sans le corps
    public List<CodeChunk> Children { get; set; } = new(); // Méthodes dans une classe
    public string ParentName { get; set; } = ""; // Nom de la classe parente pour les méthodes
}

public enum CodeChunkType
{
    Namespace,
    Class,
    Struct,
    Interface,
    Enum,
    Method,
    Property,
    Field,
    Constructor
}

/// <summary>
/// Carte complète d'un fichier avec tous ses chunks
/// </summary>
public class FileMap
{
    public string FilePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public int TotalLines { get; set; }
    public List<string> Namespaces { get; set; } = new();
    public List<string> Usings { get; set; } = new();
    public List<CodeChunk> Chunks { get; set; } = new();

    /// <summary>
    /// Résumé compact du fichier pour le contexte de l'agent
    /// </summary>
    public string GetSummary()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"📄 {FileName} ({TotalLines} lignes)");

        foreach (var chunk in Chunks.Where(c => c.Type == CodeChunkType.Class ||
                                                 c.Type == CodeChunkType.Struct ||
                                                 c.Type == CodeChunkType.Interface ||
                                                 c.Type == CodeChunkType.Enum))
        {
            sb.AppendLine($"  └─ {chunk.Type} {chunk.Name} (L{chunk.StartLine}-{chunk.EndLine})");
            foreach (var child in chunk.Children.Take(10))
            {
                sb.AppendLine($"      └─ {child.Type} {child.Name}() L{child.StartLine}-{child.EndLine}");
            }
            if (chunk.Children.Count > 10)
            {
                sb.AppendLine($"      ... et {chunk.Children.Count - 10} autres membres");
            }
        }

        return sb.ToString();
    }
}
