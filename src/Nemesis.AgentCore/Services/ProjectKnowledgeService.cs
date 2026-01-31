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

        // Extraire les informations de classe
        ExtractClassInfo(projectFile, content);

        _allFiles[relativePath] = projectFile;

        // Organiser par dossier
        if (!_filesByFolder.ContainsKey(folder))
            _filesByFolder[folder] = new List<string>();
        _filesByFolder[folder].Add(relativePath);
    }

    private void ExtractClassInfo(ProjectFile file, string content)
    {
        // Pattern pour les classes, structs, interfaces, enums
        var classPattern = @"(?:public|private|protected|internal)?\s*(?:partial\s+)?(?:abstract\s+)?(?:sealed\s+)?(?:static\s+)?(class|struct|interface|enum)\s+(\w+)(?:\s*:\s*([^{]+))?";

        foreach (Match match in Regex.Matches(content, classPattern))
        {
            var kind = match.Groups[1].Value;
            var className = match.Groups[2].Value;
            var inheritance = match.Groups[3].Value.Trim();

            var classInfo = new ClassInfo
            {
                Name = className,
                Kind = kind,
                FilePath = file.RelativePath,
                BaseClasses = ParseInheritance(inheritance),
                Methods = ExtractMethods(content, className),
                Properties = ExtractProperties(content, className),
                Fields = ExtractFields(content, className)
            };

            // Déterminer le type Unity
            classInfo.IsMonoBehaviour = classInfo.BaseClasses.Any(b =>
                b.Contains("MonoBehaviour") || b.Contains("NetworkBehaviour"));
            classInfo.IsScriptableObject = classInfo.BaseClasses.Any(b =>
                b.Contains("ScriptableObject"));

            _allClasses[className] = classInfo;
            file.Classes.Add(classInfo);
        }

        // Extraire les usings pour les dépendances
        var usingPattern = @"using\s+([\w.]+);";
        foreach (Match match in Regex.Matches(content, usingPattern))
        {
            file.Usings.Add(match.Groups[1].Value);
        }
    }

    private List<string> ParseInheritance(string inheritance)
    {
        if (string.IsNullOrWhiteSpace(inheritance))
            return new List<string>();

        return inheritance.Split(',')
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList();
    }

    private List<MethodInfo> ExtractMethods(string content, string className)
    {
        var methods = new List<MethodInfo>();

        // Pattern simplifié pour les méthodes
        var methodPattern = @"(?:public|private|protected|internal)?\s*(?:static\s+)?(?:virtual\s+)?(?:override\s+)?(?:async\s+)?(?:[\w<>\[\],\s]+)\s+(\w+)\s*\(([^)]*)\)";

        foreach (Match match in Regex.Matches(content, methodPattern))
        {
            var methodName = match.Groups[1].Value;
            var parameters = match.Groups[2].Value;

            // Ignorer les constructeurs et mots-clés
            if (methodName == className || methodName == "if" || methodName == "while" ||
                methodName == "for" || methodName == "switch" || methodName == "catch")
                continue;

            methods.Add(new MethodInfo
            {
                Name = methodName,
                Parameters = parameters,
                IsUnityCallback = IsUnityCallback(methodName)
            });
        }

        return methods.DistinctBy(m => m.Name + m.Parameters).ToList();
    }

    private bool IsUnityCallback(string methodName)
    {
        var unityCallbacks = new[] { "Awake", "Start", "Update", "FixedUpdate", "LateUpdate",
            "OnEnable", "OnDisable", "OnDestroy", "OnTriggerEnter", "OnTriggerExit",
            "OnCollisionEnter", "OnCollisionExit", "OnGUI", "OnDrawGizmos" };
        return unityCallbacks.Contains(methodName);
    }

    private List<string> ExtractProperties(string content, string className)
    {
        var props = new List<string>();
        var propPattern = @"(?:public|private|protected)\s+(?:static\s+)?(?:[\w<>\[\],\?]+)\s+(\w+)\s*\{\s*(?:get|set)";

        foreach (Match match in Regex.Matches(content, propPattern))
        {
            props.Add(match.Groups[1].Value);
        }

        return props.Distinct().ToList();
    }

    private List<string> ExtractFields(string content, string className)
    {
        var fields = new List<string>();
        var fieldPattern = @"\[(?:SerializeField|SyncVar|Header|Tooltip)[^\]]*\]\s*(?:public|private|protected)?\s*(?:[\w<>\[\],\?]+)\s+(\w+)\s*[;=]";

        foreach (Match match in Regex.Matches(content, fieldPattern))
        {
            fields.Add(match.Groups[1].Value);
        }

        return fields.Distinct().ToList();
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
    public bool IsUnityCallback { get; set; }
}

public class SearchResult
{
    public string Type { get; set; } = "";
    public string Name { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string Description { get; set; } = "";
}
