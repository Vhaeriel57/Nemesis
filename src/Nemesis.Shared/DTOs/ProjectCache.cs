namespace Nemesis.Shared.DTOs;

/// <summary>
/// Represents cached project state for persistence across sessions
/// </summary>
public class ProjectCache
{
    public string ProjectPath { get; set; } = string.Empty;
    public string ProjectName { get; set; } = string.Empty;
    public DateTime CachedAt { get; set; }
    public DateTime LastAccessedAt { get; set; }
    public ProjectInfo ProjectInfo { get; set; } = new();
    public bool HasVectorIndex { get; set; }
    public int VectorCount { get; set; }
    public string? IndexChecksum { get; set; }
}

/// <summary>
/// Stores the list of recent projects for quick access
/// </summary>
public class ProjectCacheManifest
{
    public string Version { get; set; } = "1.0";
    public string? LastUsedProjectPath { get; set; }
    public List<ProjectCacheEntry> RecentProjects { get; set; } = new();
    public DateTime LastUpdated { get; set; }
}

public class ProjectCacheEntry
{
    public string ProjectPath { get; set; } = string.Empty;
    public string ProjectName { get; set; } = string.Empty;
    public DateTime LastAccessedAt { get; set; }
    public bool IsValid { get; set; } = true;
}
