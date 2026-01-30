using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nemesis.AgentCore.Models;
using Nemesis.CodeAnalysis.Models;
using Nemesis.Shared.DTOs;

namespace Nemesis.AgentCore.Services;

/// <summary>
/// Service for caching and persisting project state across sessions
/// </summary>
public class ProjectCacheService
{
    private readonly ILogger<ProjectCacheService> _logger;
    private readonly string _cachePath;
    private readonly string _manifestPath;
    private readonly string _indexCachePath;
    private ProjectCacheManifest _manifest;
    private readonly object _lock = new();
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ProjectCacheService(NemesisConfiguration config, ILogger<ProjectCacheService> logger)
    {
        _logger = logger;
        _cachePath = Path.GetFullPath(config.Storage.CachePath);
        _indexCachePath = Path.Combine(_cachePath, "project_indexes");
        _manifestPath = Path.Combine(_cachePath, "project_manifest.json");

        Directory.CreateDirectory(_cachePath);
        Directory.CreateDirectory(_indexCachePath);

        _manifest = LoadManifest();
    }

    /// <summary>
    /// Gets the last used project path, if any
    /// </summary>
    public string? GetLastUsedProjectPath()
    {
        lock (_lock)
        {
            if (string.IsNullOrEmpty(_manifest.LastUsedProjectPath))
                return null;

            // Verify the project still exists
            if (!Directory.Exists(_manifest.LastUsedProjectPath))
            {
                _logger.LogWarning("Last used project no longer exists: {Path}", _manifest.LastUsedProjectPath);
                _manifest.LastUsedProjectPath = null;
                SaveManifest();
                return null;
            }

            return _manifest.LastUsedProjectPath;
        }
    }

    /// <summary>
    /// Gets list of recent projects
    /// </summary>
    public List<ProjectCacheEntry> GetRecentProjects()
    {
        lock (_lock)
        {
            // Validate that projects still exist
            var validProjects = _manifest.RecentProjects
                .Where(p => Directory.Exists(p.ProjectPath))
                .ToList();

            if (validProjects.Count != _manifest.RecentProjects.Count)
            {
                _manifest.RecentProjects = validProjects;
                SaveManifest();
            }

            return validProjects.OrderByDescending(p => p.LastAccessedAt).Take(10).ToList();
        }
    }

    /// <summary>
    /// Saves project index to cache
    /// </summary>
    public async Task SaveProjectIndexAsync(ProjectIndex index, ProjectInfo info)
    {
        try
        {
            var cacheFileName = GetCacheFileName(index.ProjectPath);
            var cachePath = Path.Combine(_indexCachePath, cacheFileName);

            // Calculate checksum for cache validation
            var checksum = ComputeProjectChecksum(index.ProjectPath);

            var cache = new ProjectCache
            {
                ProjectPath = index.ProjectPath,
                ProjectName = info.Name,
                CachedAt = DateTime.UtcNow,
                LastAccessedAt = DateTime.UtcNow,
                ProjectInfo = info,
                HasVectorIndex = true,
                VectorCount = 0, // Will be updated by RAG service
                IndexChecksum = checksum
            };

            // Save cache metadata
            var metadataPath = cachePath + ".meta.json";
            var metadataJson = JsonSerializer.Serialize(cache, _jsonOptions);
            await File.WriteAllTextAsync(metadataPath, metadataJson);

            // Save full index
            var indexPath = cachePath + ".index.json";
            var indexJson = JsonSerializer.Serialize(index, _jsonOptions);
            await File.WriteAllTextAsync(indexPath, indexJson);

            // Update manifest
            lock (_lock)
            {
                _manifest.LastUsedProjectPath = index.ProjectPath;
                _manifest.LastUpdated = DateTime.UtcNow;

                var existingEntry = _manifest.RecentProjects
                    .FirstOrDefault(p => p.ProjectPath == index.ProjectPath);

                if (existingEntry != null)
                {
                    existingEntry.LastAccessedAt = DateTime.UtcNow;
                    existingEntry.IsValid = true;
                }
                else
                {
                    _manifest.RecentProjects.Add(new ProjectCacheEntry
                    {
                        ProjectPath = index.ProjectPath,
                        ProjectName = info.Name,
                        LastAccessedAt = DateTime.UtcNow,
                        IsValid = true
                    });
                }

                // Keep only last 10 projects
                _manifest.RecentProjects = _manifest.RecentProjects
                    .OrderByDescending(p => p.LastAccessedAt)
                    .Take(10)
                    .ToList();

                SaveManifest();
            }

            _logger.LogInformation("Project cache saved: {Path}", index.ProjectPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save project cache: {Path}", index.ProjectPath);
        }
    }

    /// <summary>
    /// Loads cached project index if available and valid
    /// </summary>
    public async Task<(ProjectIndex? Index, ProjectCache? Cache)> LoadProjectIndexAsync(string projectPath)
    {
        try
        {
            var cacheFileName = GetCacheFileName(projectPath);
            var cachePath = Path.Combine(_indexCachePath, cacheFileName);

            var metadataPath = cachePath + ".meta.json";
            var indexPath = cachePath + ".index.json";

            if (!File.Exists(metadataPath) || !File.Exists(indexPath))
            {
                _logger.LogDebug("No cache found for project: {Path}", projectPath);
                return (null, null);
            }

            // Load metadata
            var metadataJson = await File.ReadAllTextAsync(metadataPath);
            var cache = JsonSerializer.Deserialize<ProjectCache>(metadataJson, _jsonOptions);

            if (cache == null)
            {
                _logger.LogWarning("Failed to deserialize cache metadata: {Path}", projectPath);
                return (null, null);
            }

            // Validate cache - check if project files have changed
            var currentChecksum = ComputeProjectChecksum(projectPath);
            if (cache.IndexChecksum != currentChecksum)
            {
                _logger.LogInformation("Project files have changed, cache invalidated: {Path}", projectPath);
                return (null, cache); // Return cache info but no index
            }

            // Load full index
            var indexJson = await File.ReadAllTextAsync(indexPath);
            var index = JsonSerializer.Deserialize<ProjectIndex>(indexJson, _jsonOptions);

            if (index == null)
            {
                _logger.LogWarning("Failed to deserialize project index: {Path}", projectPath);
                return (null, cache);
            }

            // Update last accessed time
            cache.LastAccessedAt = DateTime.UtcNow;
            var updatedMetadata = JsonSerializer.Serialize(cache, _jsonOptions);
            await File.WriteAllTextAsync(metadataPath, updatedMetadata);

            // Update manifest
            lock (_lock)
            {
                _manifest.LastUsedProjectPath = projectPath;
                var entry = _manifest.RecentProjects.FirstOrDefault(p => p.ProjectPath == projectPath);
                if (entry != null)
                {
                    entry.LastAccessedAt = DateTime.UtcNow;
                }
                SaveManifest();
            }

            _logger.LogInformation("Project cache loaded successfully: {Path} ({Files} files, {Types} types)",
                projectPath, index.TotalFiles, index.TotalTypes);

            return (index, cache);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load project cache: {Path}", projectPath);
            return (null, null);
        }
    }

    /// <summary>
    /// Checks if a valid cache exists for the project
    /// </summary>
    public async Task<bool> HasValidCacheAsync(string projectPath)
    {
        var (index, _) = await LoadProjectIndexAsync(projectPath);
        return index != null;
    }

    /// <summary>
    /// Clears cache for a specific project
    /// </summary>
    public void ClearProjectCache(string projectPath)
    {
        try
        {
            var cacheFileName = GetCacheFileName(projectPath);
            var cachePath = Path.Combine(_indexCachePath, cacheFileName);

            var metadataPath = cachePath + ".meta.json";
            var indexPath = cachePath + ".index.json";

            if (File.Exists(metadataPath)) File.Delete(metadataPath);
            if (File.Exists(indexPath)) File.Delete(indexPath);

            lock (_lock)
            {
                _manifest.RecentProjects.RemoveAll(p => p.ProjectPath == projectPath);
                if (_manifest.LastUsedProjectPath == projectPath)
                    _manifest.LastUsedProjectPath = _manifest.RecentProjects.FirstOrDefault()?.ProjectPath;
                SaveManifest();
            }

            _logger.LogInformation("Project cache cleared: {Path}", projectPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear project cache: {Path}", projectPath);
        }
    }

    /// <summary>
    /// Clears all cached projects
    /// </summary>
    public void ClearAllCache()
    {
        try
        {
            if (Directory.Exists(_indexCachePath))
            {
                foreach (var file in Directory.GetFiles(_indexCachePath))
                {
                    File.Delete(file);
                }
            }

            lock (_lock)
            {
                _manifest = new ProjectCacheManifest();
                SaveManifest();
            }

            _logger.LogInformation("All project caches cleared");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear all project caches");
        }
    }

    private ProjectCacheManifest LoadManifest()
    {
        try
        {
            if (File.Exists(_manifestPath))
            {
                var json = File.ReadAllText(_manifestPath);
                return JsonSerializer.Deserialize<ProjectCacheManifest>(json, _jsonOptions)
                    ?? new ProjectCacheManifest();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load project manifest");
        }

        return new ProjectCacheManifest();
    }

    private void SaveManifest()
    {
        try
        {
            var json = JsonSerializer.Serialize(_manifest, _jsonOptions);
            File.WriteAllText(_manifestPath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save project manifest");
        }
    }

    private string GetCacheFileName(string projectPath)
    {
        // Create a safe filename from the project path
        using var sha = SHA256.Create();
        var hashBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(projectPath.ToLowerInvariant()));
        var hash = Convert.ToHexString(hashBytes)[..16];
        var safeName = Path.GetFileName(projectPath) ?? "project";
        return $"{safeName}_{hash}";
    }

    private string ComputeProjectChecksum(string projectPath)
    {
        try
        {
            // Compute a checksum based on file count, total size, and latest modification
            var csFiles = Directory.GetFiles(projectPath, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains("Library") && !f.Contains("Temp") && !f.Contains("obj"))
                .ToList();

            var fileCount = csFiles.Count;
            var totalSize = csFiles.Sum(f => new FileInfo(f).Length);
            var latestModified = csFiles.Any()
                ? csFiles.Max(f => File.GetLastWriteTimeUtc(f))
                : DateTime.MinValue;

            return $"{fileCount}_{totalSize}_{latestModified:yyyyMMddHHmmss}";
        }
        catch
        {
            return Guid.NewGuid().ToString(); // Invalid checksum forces re-index
        }
    }
}
