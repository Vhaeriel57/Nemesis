using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Nemesis.Shared.Models;

namespace Nemesis.AgentCore.Services;

/// <summary>
/// Service de persistance pour sauvegarder TOUT l'état de Nemesis
/// - Projets et leur état
/// - Historique des conversations
/// - Mémoire de l'agent (erreurs, corrections, apprentissages)
/// - Points de sauvegarde des patches
/// </summary>
public class PersistenceService
{
    private readonly ILogger<PersistenceService> _logger;
    private readonly string _dataPath;
    private readonly JsonSerializerOptions _jsonOptions;

    // Données en mémoire
    private NemesisData _data = new();
    private bool _isLoaded = false;

    public PersistenceService(ILogger<PersistenceService> logger)
    {
        _logger = logger;

        // Dossier de données persistantes
        _dataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Nemesis"
        );

        Directory.CreateDirectory(_dataPath);

        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    public string DataPath => _dataPath;
    public bool IsLoaded => _isLoaded;
    public List<ProjectState> Projects => _data.Projects;
    public ProjectState? CurrentProject => _data.Projects.FirstOrDefault(p => p.Id == _data.CurrentProjectId);

    #region Initialization

    /// <summary>
    /// Charge toutes les données persistantes au démarrage
    /// </summary>
    public async Task LoadAsync()
    {
        try
        {
            var dataFile = Path.Combine(_dataPath, "nemesis_data.json");
            if (File.Exists(dataFile))
            {
                var json = await File.ReadAllTextAsync(dataFile);
                _data = JsonSerializer.Deserialize<NemesisData>(json, _jsonOptions) ?? new NemesisData();
                _logger.LogInformation("Données chargées: {Projects} projets, {Memory} entrées mémoire",
                    _data.Projects.Count, _data.AgentMemory.Count);
            }
            else
            {
                _data = new NemesisData();
                _logger.LogInformation("Première utilisation - création des données");
            }

            _isLoaded = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur chargement données persistantes");
            _data = new NemesisData();
            _isLoaded = true;
        }
    }

    /// <summary>
    /// Sauvegarde toutes les données
    /// </summary>
    public async Task SaveAsync()
    {
        try
        {
            var dataFile = Path.Combine(_dataPath, "nemesis_data.json");
            var json = JsonSerializer.Serialize(_data, _jsonOptions);
            await File.WriteAllTextAsync(dataFile, json);
            _logger.LogDebug("Données sauvegardées");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur sauvegarde données");
        }
    }

    #endregion

    #region Project Management

    /// <summary>
    /// Ajoute ou met à jour un projet
    /// </summary>
    public async Task<ProjectState> AddOrUpdateProjectAsync(string projectPath, string? name = null)
    {
        var existing = _data.Projects.FirstOrDefault(p => p.Path == projectPath);

        if (existing != null)
        {
            existing.LastOpenedAt = DateTime.UtcNow;
            existing.OpenCount++;
            await SaveAsync();
            return existing;
        }

        var project = new ProjectState
        {
            Id = Guid.NewGuid().ToString(),
            Name = name ?? Path.GetFileName(projectPath),
            Path = projectPath,
            CreatedAt = DateTime.UtcNow,
            LastOpenedAt = DateTime.UtcNow,
            OpenCount = 1
        };

        _data.Projects.Add(project);
        _data.CurrentProjectId = project.Id;
        await SaveAsync();

        _logger.LogInformation("Projet ajouté: {Name} ({Path})", project.Name, project.Path);
        return project;
    }

    /// <summary>
    /// Sélectionne un projet comme projet courant
    /// </summary>
    public async Task SelectProjectAsync(string projectId)
    {
        var project = _data.Projects.FirstOrDefault(p => p.Id == projectId);
        if (project != null)
        {
            _data.CurrentProjectId = projectId;
            project.LastOpenedAt = DateTime.UtcNow;
            project.OpenCount++;
            await SaveAsync();
        }
    }

    /// <summary>
    /// Récupère tous les projets
    /// </summary>
    public List<ProjectState> GetAllProjects()
    {
        return _data.Projects.OrderByDescending(p => p.LastOpenedAt).ToList();
    }

    /// <summary>
    /// Supprime un projet de la liste (ne supprime pas les fichiers)
    /// </summary>
    public async Task RemoveProjectAsync(string projectId)
    {
        _data.Projects.RemoveAll(p => p.Id == projectId);
        if (_data.CurrentProjectId == projectId)
            _data.CurrentProjectId = _data.Projects.FirstOrDefault()?.Id;
        await SaveAsync();
    }

    #endregion

    #region Chat History

    /// <summary>
    /// Ajoute un message à l'historique du projet courant
    /// </summary>
    public async Task AddChatMessageAsync(string projectId, ChatMessage message)
    {
        var project = _data.Projects.FirstOrDefault(p => p.Id == projectId);
        if (project == null) return;

        project.ChatHistory.Add(new PersistedChatMessage
        {
            Role = message.Role,
            Content = message.Content,
            Timestamp = DateTime.UtcNow
        });

        // Garder les 500 derniers messages max
        if (project.ChatHistory.Count > 500)
        {
            project.ChatHistory = project.ChatHistory.TakeLast(500).ToList();
        }

        await SaveAsync();
    }

    /// <summary>
    /// Récupère l'historique de chat d'un projet
    /// </summary>
    public List<ChatMessage> GetChatHistory(string projectId)
    {
        var project = _data.Projects.FirstOrDefault(p => p.Id == projectId);
        if (project == null) return new List<ChatMessage>();

        return project.ChatHistory.Select(m => new ChatMessage
        {
            Role = m.Role,
            Content = m.Content
        }).ToList();
    }

    /// <summary>
    /// Efface l'historique de chat d'un projet
    /// </summary>
    public async Task ClearChatHistoryAsync(string projectId)
    {
        var project = _data.Projects.FirstOrDefault(p => p.Id == projectId);
        if (project != null)
        {
            project.ChatHistory.Clear();
            await SaveAsync();
        }
    }

    #endregion

    #region Agent Memory (Apprentissage)

    /// <summary>
    /// Ajoute une entrée dans la mémoire de l'agent
    /// </summary>
    public async Task AddMemoryEntryAsync(MemoryEntry entry)
    {
        entry.Id = Guid.NewGuid().ToString();
        entry.CreatedAt = DateTime.UtcNow;
        _data.AgentMemory.Add(entry);

        // Garder les 1000 dernières entrées
        if (_data.AgentMemory.Count > 1000)
        {
            _data.AgentMemory = _data.AgentMemory
                .OrderByDescending(m => m.Importance)
                .ThenByDescending(m => m.CreatedAt)
                .Take(1000)
                .ToList();
        }

        await SaveAsync();
        _logger.LogDebug("Mémoire ajoutée: {Type} - {Summary}", entry.Type, entry.Summary);
    }

    /// <summary>
    /// Enregistre une erreur et sa correction
    /// </summary>
    public async Task RecordErrorAndCorrectionAsync(string error, string correction, string context)
    {
        await AddMemoryEntryAsync(new MemoryEntry
        {
            Type = MemoryType.ErrorCorrection,
            Summary = $"Erreur: {error.Substring(0, Math.Min(100, error.Length))}...",
            Content = $"## Erreur\n{error}\n\n## Correction\n{correction}\n\n## Contexte\n{context}",
            Tags = new List<string> { "erreur", "correction" },
            Importance = 8
        });
    }

    /// <summary>
    /// Enregistre un apprentissage
    /// </summary>
    public async Task RecordLearningAsync(string topic, string learning, List<string>? tags = null)
    {
        await AddMemoryEntryAsync(new MemoryEntry
        {
            Type = MemoryType.Learning,
            Summary = topic,
            Content = learning,
            Tags = tags ?? new List<string>(),
            Importance = 5
        });
    }

    /// <summary>
    /// Enregistre une note sur le projet
    /// </summary>
    public async Task RecordProjectNoteAsync(string projectId, string note, string category)
    {
        await AddMemoryEntryAsync(new MemoryEntry
        {
            Type = MemoryType.ProjectNote,
            ProjectId = projectId,
            Summary = category,
            Content = note,
            Tags = new List<string> { category },
            Importance = 6
        });
    }

    /// <summary>
    /// Recherche dans la mémoire
    /// </summary>
    public List<MemoryEntry> SearchMemory(string query, string? projectId = null, MemoryType? type = null, int limit = 20)
    {
        var queryLower = query.ToLower();

        return _data.AgentMemory
            .Where(m =>
                (projectId == null || m.ProjectId == projectId || m.ProjectId == null) &&
                (type == null || m.Type == type) &&
                (m.Summary.ToLower().Contains(queryLower) ||
                 m.Content.ToLower().Contains(queryLower) ||
                 m.Tags.Any(t => t.ToLower().Contains(queryLower))))
            .OrderByDescending(m => m.Importance)
            .ThenByDescending(m => m.CreatedAt)
            .Take(limit)
            .ToList();
    }

    /// <summary>
    /// Récupère la mémoire pertinente pour un contexte donné
    /// </summary>
    public string GetRelevantMemoryContext(string query, string? projectId = null, int maxChars = 2000)
    {
        var memories = SearchMemory(query, projectId, limit: 10);

        if (!memories.Any())
            return "";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## 🧠 MÉMOIRE DE L'AGENT (apprentissages précédents)");
        sb.AppendLine();

        foreach (var memory in memories)
        {
            if (sb.Length > maxChars) break;

            var icon = memory.Type switch
            {
                MemoryType.ErrorCorrection => "⚠️",
                MemoryType.Learning => "💡",
                MemoryType.ProjectNote => "📝",
                MemoryType.Pattern => "🔄",
                _ => "📌"
            };

            sb.AppendLine($"{icon} **{memory.Summary}**");
            sb.AppendLine(memory.Content.Length > 300
                ? memory.Content.Substring(0, 300) + "..."
                : memory.Content);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    #endregion

    #region Savepoints (Points de sauvegarde)

    /// <summary>
    /// Crée un point de sauvegarde avant un patch
    /// </summary>
    public async Task<Savepoint> CreateSavepointAsync(string projectId, string description, List<FilePatch>? patches = null)
    {
        var project = _data.Projects.FirstOrDefault(p => p.Id == projectId);
        if (project == null)
            throw new InvalidOperationException("Projet non trouvé");

        var savepoint = new Savepoint
        {
            Id = Guid.NewGuid().ToString(),
            ProjectId = projectId,
            Description = description,
            CreatedAt = DateTime.UtcNow,
            Patches = patches ?? new List<FilePatch>()
        };

        project.Savepoints.Add(savepoint);

        // Garder les 50 derniers savepoints
        if (project.Savepoints.Count > 50)
        {
            project.Savepoints = project.Savepoints.TakeLast(50).ToList();
        }

        await SaveAsync();
        _logger.LogInformation("Savepoint créé: {Description}", description);

        return savepoint;
    }

    /// <summary>
    /// Récupère les savepoints d'un projet
    /// </summary>
    public List<Savepoint> GetSavepoints(string projectId)
    {
        var project = _data.Projects.FirstOrDefault(p => p.Id == projectId);
        return project?.Savepoints.OrderByDescending(s => s.CreatedAt).ToList() ?? new List<Savepoint>();
    }

    #endregion

    #region Agent Documentation

    /// <summary>
    /// Met à jour la documentation interne de l'agent pour un projet
    /// </summary>
    public async Task UpdateProjectDocumentationAsync(string projectId, string section, string content)
    {
        var project = _data.Projects.FirstOrDefault(p => p.Id == projectId);
        if (project == null) return;

        project.Documentation[section] = new DocumentationSection
        {
            Title = section,
            Content = content,
            LastUpdated = DateTime.UtcNow
        };

        await SaveAsync();
    }

    /// <summary>
    /// Récupère la documentation d'un projet
    /// </summary>
    public string GetProjectDocumentation(string projectId)
    {
        var project = _data.Projects.FirstOrDefault(p => p.Id == projectId);
        if (project == null || !project.Documentation.Any())
            return "";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## 📚 DOCUMENTATION INTERNE DU PROJET");
        sb.AppendLine();

        foreach (var doc in project.Documentation.Values.OrderBy(d => d.Title))
        {
            sb.AppendLine($"### {doc.Title}");
            sb.AppendLine(doc.Content);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Ajoute une note rapide à la documentation
    /// </summary>
    public async Task AddQuickNoteAsync(string projectId, string note)
    {
        var project = _data.Projects.FirstOrDefault(p => p.Id == projectId);
        if (project == null) return;

        if (!project.Documentation.ContainsKey("Notes"))
        {
            project.Documentation["Notes"] = new DocumentationSection
            {
                Title = "Notes",
                Content = "",
                LastUpdated = DateTime.UtcNow
            };
        }

        project.Documentation["Notes"].Content += $"\n- [{DateTime.Now:HH:mm}] {note}";
        project.Documentation["Notes"].LastUpdated = DateTime.UtcNow;

        await SaveAsync();
    }

    #endregion
}

#region Data Models

/// <summary>
/// Données principales de Nemesis
/// </summary>
public class NemesisData
{
    public string? CurrentProjectId { get; set; }
    public List<ProjectState> Projects { get; set; } = new();
    public List<MemoryEntry> AgentMemory { get; set; } = new();
    public DateTime LastSaved { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// État complet d'un projet
/// </summary>
public class ProjectState
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime LastOpenedAt { get; set; }
    public int OpenCount { get; set; }

    // Historique des conversations
    public List<PersistedChatMessage> ChatHistory { get; set; } = new();

    // Points de sauvegarde
    public List<Savepoint> Savepoints { get; set; } = new();

    // Documentation interne
    public Dictionary<string, DocumentationSection> Documentation { get; set; } = new();

    // Métadonnées du projet
    public ProjectMetadata Metadata { get; set; } = new();
}

public class PersistedChatMessage
{
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";
    public DateTime Timestamp { get; set; }
}

public class ProjectMetadata
{
    public int TotalFiles { get; set; }
    public int TotalClasses { get; set; }
    public List<string> MainNamespaces { get; set; } = new();
    public DateTime LastIndexed { get; set; }
}

/// <summary>
/// Point de sauvegarde
/// </summary>
public class Savepoint
{
    public string Id { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public List<FilePatch> Patches { get; set; } = new();
    public Dictionary<string, string> FileBackups { get; set; } = new(); // path -> content backup
}

/// <summary>
/// Entrée dans la mémoire de l'agent
/// </summary>
public class MemoryEntry
{
    public string Id { get; set; } = "";
    public MemoryType Type { get; set; }
    public string? ProjectId { get; set; } // null = global
    public string Summary { get; set; } = "";
    public string Content { get; set; } = "";
    public List<string> Tags { get; set; } = new();
    public int Importance { get; set; } = 5; // 1-10
    public DateTime CreatedAt { get; set; }
}

public enum MemoryType
{
    ErrorCorrection,  // Erreur et sa correction
    Learning,         // Apprentissage général
    ProjectNote,      // Note spécifique au projet
    Pattern,          // Pattern de code reconnu
    UserPreference    // Préférence utilisateur
}

/// <summary>
/// Section de documentation
/// </summary>
public class DocumentationSection
{
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public DateTime LastUpdated { get; set; }
}

#endregion
