using Nemesis.CodeAnalysis.Models;
using Nemesis.Shared.DTOs;

namespace Nemesis.CodeAnalysis.Indexing;

public class ProjectScanner
{
    private readonly List<string> _excludedFolders;
    private readonly List<string> _excludedExtensions;

    public ProjectScanner(List<string>? excludedFolders = null, List<string>? excludedExtensions = null)
    {
        _excludedFolders = excludedFolders ?? new List<string>
        {
            "Library", "Temp", "obj", "bin", "Logs", ".git", ".vs", "Builds", "Build", "UserSettings"
        };
        _excludedExtensions = excludedExtensions ?? new List<string>
        {
            ".meta", ".asset", ".prefab", ".unity", ".mat", ".shader"
        };
    }

    public async Task<List<CodeFile>> ScanProjectAsync(
        string projectPath,
        IProgress<IndexingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var files = new List<CodeFile>();
        var csFiles = GetCSharpFiles(projectPath);
        var total = csFiles.Count;
        var current = 0;

        foreach (var filePath in csFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var fileInfo = new FileInfo(filePath);
                var content = await File.ReadAllTextAsync(filePath, cancellationToken);
                var relativePath = Path.GetRelativePath(projectPath, filePath);

                files.Add(new CodeFile
                {
                    FilePath = filePath,
                    RelativePath = relativePath,
                    Content = content,
                    LastModified = fileInfo.LastWriteTimeUtc,
                    SizeBytes = fileInfo.Length,
                    LineCount = content.Split('\n').Length
                });

                current++;
                progress?.Report(new IndexingProgress
                {
                    Phase = "Scanning files",
                    Current = current,
                    Total = total,
                    CurrentFile = relativePath
                });
            }
            catch (Exception ex)
            {
                files.Add(new CodeFile
                {
                    FilePath = filePath,
                    RelativePath = Path.GetRelativePath(projectPath, filePath),
                    Errors = new List<string> { ex.Message }
                });
            }
        }

        return files;
    }

    private List<string> GetCSharpFiles(string projectPath)
    {
        var files = new List<string>();

        try
        {
            var allFiles = Directory.GetFiles(projectPath, "*.cs", SearchOption.AllDirectories);

            foreach (var file in allFiles)
            {
                var relativePath = Path.GetRelativePath(projectPath, file);
                var pathParts = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                var isExcluded = pathParts.Any(part =>
                    _excludedFolders.Any(ef => ef.Equals(part, StringComparison.OrdinalIgnoreCase)));

                var extension = Path.GetExtension(file);
                isExcluded = isExcluded || _excludedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);

                if (!isExcluded)
                {
                    files.Add(file);
                }
            }
        }
        catch (Exception)
        {
            // Directory might not exist or access denied
        }

        return files;
    }

    public bool IsPathAllowed(string basePath, string targetPath)
    {
        var fullBase = Path.GetFullPath(basePath);
        var fullTarget = Path.GetFullPath(targetPath);
        return fullTarget.StartsWith(fullBase, StringComparison.OrdinalIgnoreCase);
    }
}
