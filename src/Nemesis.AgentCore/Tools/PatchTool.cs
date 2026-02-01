using System.Text;
using Nemesis.Shared.DTOs;
using Nemesis.Shared.Interfaces;
using Nemesis.Shared.Models;

namespace Nemesis.AgentCore.Tools;

public class PatchTool : ITool, IPatchService
{
    private readonly string _backupPath;
    private readonly bool _autoBackup;

    public string Name => "patch";
    public string Description => "Create, preview, and apply code patches. Always creates backups before modifications.";

    public ToolDefinition Definition => new()
    {
        Name = Name,
        Description = Description,
        Parameters = new Dictionary<string, ToolParameter>
        {
            ["action"] = new()
            {
                Type = "string",
                Description = "The action to perform",
                Enum = new List<string> { "create", "preview", "apply", "rollback" }
            },
            ["file_path"] = new()
            {
                Type = "string",
                Description = "The file to patch (relative to project root)"
            },
            ["original_content"] = new()
            {
                Type = "string",
                Description = "The original code to replace (for create action)"
            },
            ["modified_content"] = new()
            {
                Type = "string",
                Description = "The new code to insert (for create action)"
            },
            ["patch_id"] = new()
            {
                Type = "string",
                Description = "Patch ID (for apply/rollback actions)"
            }
        },
        Required = new List<string> { "action" }
    };

    private readonly Dictionary<string, FilePatch> _pendingPatches = new();
    private readonly List<FilePatch> _appliedPatchesHistory = new();

    public PatchTool(string? backupPath = null, bool autoBackup = true)
    {
        _backupPath = backupPath ?? "./backups";
        _autoBackup = autoBackup;
        Directory.CreateDirectory(_backupPath);
    }

    public async Task<ToolResult> ExecuteAsync(
        Dictionary<string, object> parameters,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        var action = parameters.GetValueOrDefault("action")?.ToString() ?? "";

        return action.ToLower() switch
        {
            "create" => await CreatePatchAsync(parameters, context, cancellationToken),
            "preview" => PreviewPatch(parameters),
            "apply" => await ApplyPatchToolAsync(parameters, context, cancellationToken),
            "rollback" => await RollbackPatchToolAsync(parameters, context, cancellationToken),
            _ => new ToolResult { Success = false, Error = $"Unknown action: {action}" }
        };
    }

    private async Task<ToolResult> CreatePatchAsync(
        Dictionary<string, object> parameters,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        var filePath = parameters.GetValueOrDefault("file_path")?.ToString();
        var originalContent = parameters.GetValueOrDefault("original_content")?.ToString();
        var modifiedContent = parameters.GetValueOrDefault("modified_content")?.ToString();

        if (string.IsNullOrEmpty(filePath))
        {
            return new ToolResult
            {
                Success = false,
                Error = "file_path is required"
            };
        }

        var fullPath = Path.Combine(context.ProjectPath, filePath);

        if (!File.Exists(fullPath))
        {
            return new ToolResult
            {
                Success = false,
                Error = $"File not found: {filePath}"
            };
        }

        var fileContent = await File.ReadAllTextAsync(fullPath, cancellationToken);

        // If original_content and modified_content provided, do search/replace
        if (!string.IsNullOrEmpty(originalContent) && !string.IsNullOrEmpty(modifiedContent))
        {
            if (!fileContent.Contains(originalContent))
            {
                return new ToolResult
                {
                    Success = false,
                    Error = "Original content not found in file. Make sure the content matches exactly."
                };
            }

            var newContent = fileContent.Replace(originalContent, modifiedContent);
            var patch = CreatePatch(fullPath, fileContent, newContent);
            _pendingPatches[patch.Id] = patch;

            return new ToolResult
            {
                Success = true,
                Output = $"Patch created with ID: {patch.Id}\n\n{patch.UnifiedDiff}",
                Data = patch
            };
        }
        else if (!string.IsNullOrEmpty(modifiedContent))
        {
            // Full file replacement
            var patch = CreatePatch(fullPath, fileContent, modifiedContent);
            _pendingPatches[patch.Id] = patch;

            return new ToolResult
            {
                Success = true,
                Output = $"Patch created with ID: {patch.Id}\n\n{patch.UnifiedDiff}",
                Data = patch
            };
        }

        return new ToolResult
        {
            Success = false,
            Error = "Either modified_content alone (full replacement) or both original_content and modified_content (search/replace) are required"
        };
    }

    private ToolResult PreviewPatch(Dictionary<string, object> parameters)
    {
        var patchId = parameters.GetValueOrDefault("patch_id")?.ToString();

        if (string.IsNullOrEmpty(patchId))
        {
            var allPatches = _pendingPatches.Values.Select(p => new
            {
                p.Id,
                p.FilePath,
                p.Status,
                p.CreatedAt,
                DiffPreview = p.UnifiedDiff.Length > 500
                    ? p.UnifiedDiff.Substring(0, 500) + "..."
                    : p.UnifiedDiff
            }).ToList();

            return new ToolResult
            {
                Success = true,
                Output = $"Pending patches: {allPatches.Count}\n\n" +
                        string.Join("\n\n", allPatches.Select(p => $"[{p.Id}] {p.FilePath}\n{p.DiffPreview}")),
                Data = allPatches
            };
        }

        if (!_pendingPatches.TryGetValue(patchId, out var patch))
        {
            return new ToolResult
            {
                Success = false,
                Error = $"Patch not found: {patchId}"
            };
        }

        return new ToolResult
        {
            Success = true,
            Output = $"Patch {patch.Id} for {patch.FilePath}:\n\n{patch.UnifiedDiff}",
            Data = patch
        };
    }

    private async Task<ToolResult> ApplyPatchToolAsync(
        Dictionary<string, object> parameters,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        var patchId = parameters.GetValueOrDefault("patch_id")?.ToString();

        if (string.IsNullOrEmpty(patchId))
        {
            return new ToolResult
            {
                Success = false,
                Error = "patch_id is required"
            };
        }

        if (!_pendingPatches.TryGetValue(patchId, out var patch))
        {
            return new ToolResult
            {
                Success = false,
                Error = $"Patch not found: {patchId}"
            };
        }

        var success = await ApplyPatchAsync(patch, _autoBackup, cancellationToken);

        return new ToolResult
        {
            Success = success,
            Output = success
                ? $"Patch {patchId} applied successfully to {patch.FilePath}"
                : $"Failed to apply patch: {patch.ErrorMessage}",
            AffectedFiles = new List<string> { patch.FilePath }
        };
    }

    private async Task<ToolResult> RollbackPatchToolAsync(
        Dictionary<string, object> parameters,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        var patchId = parameters.GetValueOrDefault("patch_id")?.ToString();

        if (string.IsNullOrEmpty(patchId))
        {
            return new ToolResult
            {
                Success = false,
                Error = "patch_id is required"
            };
        }

        if (!_pendingPatches.TryGetValue(patchId, out var patch))
        {
            return new ToolResult
            {
                Success = false,
                Error = $"Patch not found: {patchId}"
            };
        }

        var success = await RollbackPatchAsync(patch, cancellationToken);

        return new ToolResult
        {
            Success = success,
            Output = success
                ? $"Patch {patchId} rolled back successfully"
                : $"Failed to rollback patch: {patch.ErrorMessage}",
            AffectedFiles = new List<string> { patch.FilePath }
        };
    }

    public FilePatch CreatePatch(string filePath, string originalContent, string modifiedContent)
    {
        var hunks = ComputeDiffHunks(originalContent, modifiedContent);
        var unifiedDiff = GenerateUnifiedDiff(filePath, originalContent, modifiedContent, hunks);

        return new FilePatch
        {
            FilePath = filePath,
            OriginalContent = originalContent,
            ModifiedContent = modifiedContent,
            UnifiedDiff = unifiedDiff,
            Hunks = hunks,
            Status = PatchStatus.Pending
        };
    }

    public PatchSet CreatePatchSet(string description, List<(string filePath, string original, string modified)> changes)
    {
        var patchSet = new PatchSet
        {
            Description = description,
            Patches = changes.Select(c => CreatePatch(c.filePath, c.original, c.modified)).ToList()
        };

        return patchSet;
    }

    public async Task<bool> ApplyPatchAsync(
        FilePatch patch,
        bool createBackup = true,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Ensure we have valid file path
            if (string.IsNullOrEmpty(patch.FilePath) || !File.Exists(patch.FilePath))
            {
                patch.Status = PatchStatus.Failed;
                patch.ErrorMessage = $"File not found: {patch.FilePath}";
                return false;
            }

            // Read original content if missing
            if (string.IsNullOrEmpty(patch.OriginalContent))
            {
                patch.OriginalContent = await File.ReadAllTextAsync(patch.FilePath, cancellationToken);
            }

            // If ModifiedContent is empty but we have a UnifiedDiff, reconstruct it
            if (string.IsNullOrEmpty(patch.ModifiedContent) && !string.IsNullOrEmpty(patch.UnifiedDiff))
            {
                patch.ModifiedContent = ApplyUnifiedDiff(patch.OriginalContent, patch.UnifiedDiff);
            }

            // Still empty? Nothing to apply
            if (string.IsNullOrEmpty(patch.ModifiedContent))
            {
                patch.Status = PatchStatus.Failed;
                patch.ErrorMessage = "No modified content and could not reconstruct from diff";
                return false;
            }

            if (createBackup)
            {
                patch.BackupPath = await CreateBackupAsync(patch.FilePath, cancellationToken);
            }

            await File.WriteAllTextAsync(patch.FilePath, patch.ModifiedContent, cancellationToken);
            patch.Status = PatchStatus.Applied;

            // Add to applied history
            _appliedPatchesHistory.Add(patch);

            return true;
        }
        catch (Exception ex)
        {
            patch.Status = PatchStatus.Failed;
            patch.ErrorMessage = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Reconstructs the modified content by applying a unified diff to the original content.
    /// </summary>
    private string ApplyUnifiedDiff(string originalContent, string unifiedDiff)
    {
        var originalLines = originalContent.Split('\n').ToList();
        var diffLines = unifiedDiff.Split('\n');

        // Parse the hunks from the diff and apply them in reverse order
        // to preserve line numbers
        var hunks = new List<(int startLine, List<string> removedLines, List<string> addedLines)>();

        int? currentHunkStart = null;
        var currentRemoved = new List<string>();
        var currentAdded = new List<string>();

        foreach (var line in diffLines)
        {
            // Parse hunk header: @@ -startOrig,count +startMod,count @@
            if (line.StartsWith("@@"))
            {
                // Save previous hunk
                if (currentHunkStart.HasValue)
                {
                    hunks.Add((currentHunkStart.Value, new List<string>(currentRemoved), new List<string>(currentAdded)));
                }

                var match = System.Text.RegularExpressions.Regex.Match(line, @"@@ -(\d+)");
                currentHunkStart = match.Success ? int.Parse(match.Groups[1].Value) : null;
                currentRemoved.Clear();
                currentAdded.Clear();
            }
            else if (line.StartsWith("-") && !line.StartsWith("---"))
            {
                currentRemoved.Add(line.Substring(1));
            }
            else if (line.StartsWith("+") && !line.StartsWith("+++"))
            {
                currentAdded.Add(line.Substring(1));
            }
        }

        // Save last hunk
        if (currentHunkStart.HasValue)
        {
            hunks.Add((currentHunkStart.Value, new List<string>(currentRemoved), new List<string>(currentAdded)));
        }

        // Apply hunks in reverse order to preserve line numbers
        hunks.Reverse();
        foreach (var (startLine, removedLines, addedLines) in hunks)
        {
            var index = startLine - 1; // 0-based

            // Remove the old lines
            if (removedLines.Count > 0 && index < originalLines.Count)
            {
                var removeCount = Math.Min(removedLines.Count, originalLines.Count - index);
                originalLines.RemoveRange(index, removeCount);
            }

            // Insert the new lines
            if (addedLines.Count > 0)
            {
                var insertIndex = Math.Min(index, originalLines.Count);
                originalLines.InsertRange(insertIndex, addedLines);
            }
        }

        return string.Join("\n", originalLines);
    }

    public async Task<bool> ApplyPatchSetAsync(
        PatchSet patchSet,
        bool createBackup = true,
        CancellationToken cancellationToken = default)
    {
        var appliedPatches = new List<FilePatch>();

        try
        {
            foreach (var patch in patchSet.Patches)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var success = await ApplyPatchAsync(patch, createBackup, cancellationToken);
                if (!success)
                {
                    // Rollback already applied patches
                    foreach (var applied in appliedPatches)
                    {
                        await RollbackPatchAsync(applied, cancellationToken);
                    }
                    return false;
                }
                appliedPatches.Add(patch);
            }

            patchSet.IsApplied = true;
            return true;
        }
        catch
        {
            // Rollback on exception
            foreach (var applied in appliedPatches)
            {
                await RollbackPatchAsync(applied, cancellationToken);
            }
            return false;
        }
    }

    public async Task<bool> RollbackPatchAsync(
        FilePatch patch,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!string.IsNullOrEmpty(patch.BackupPath) && File.Exists(patch.BackupPath))
            {
                await RestoreBackupAsync(patch.BackupPath, patch.FilePath, cancellationToken);
            }
            else
            {
                await File.WriteAllTextAsync(patch.FilePath, patch.OriginalContent, cancellationToken);
            }

            patch.Status = PatchStatus.RolledBack;
            return true;
        }
        catch (Exception ex)
        {
            patch.ErrorMessage = ex.Message;
            return false;
        }
    }

    public async Task<bool> RollbackPatchSetAsync(
        PatchSet patchSet,
        CancellationToken cancellationToken = default)
    {
        var success = true;

        foreach (var patch in patchSet.Patches.Where(p => p.Status == PatchStatus.Applied))
        {
            if (!await RollbackPatchAsync(patch, cancellationToken))
            {
                success = false;
            }
        }

        if (success)
        {
            patchSet.IsApplied = false;
        }

        return success;
    }

    public Task<string> CreateBackupAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var fileName = Path.GetFileName(filePath);
        var backupFileName = $"{fileName}.{timestamp}.bak";
        var backupPath = Path.Combine(_backupPath, backupFileName);

        File.Copy(filePath, backupPath, overwrite: true);
        return Task.FromResult(backupPath);
    }

    public Task<bool> RestoreBackupAsync(
        string backupPath,
        string originalPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(backupPath))
            return Task.FromResult(false);

        File.Copy(backupPath, originalPath, overwrite: true);
        return Task.FromResult(true);
    }

    public Task CleanupOldBackupsAsync(
        int maxAgeDays,
        CancellationToken cancellationToken = default)
    {
        var cutoff = DateTime.UtcNow.AddDays(-maxAgeDays);
        var files = Directory.GetFiles(_backupPath, "*.bak");

        foreach (var file in files)
        {
            var fileInfo = new FileInfo(file);
            if (fileInfo.CreationTimeUtc < cutoff)
            {
                File.Delete(file);
            }
        }

        return Task.CompletedTask;
    }

    private List<DiffHunk> ComputeDiffHunks(string original, string modified)
    {
        var originalLines = original.Split('\n');
        var modifiedLines = modified.Split('\n');
        var hunks = new List<DiffHunk>();

        // Simple LCS-based diff
        var lcs = ComputeLcs(originalLines, modifiedLines);
        var hunk = new DiffHunk { Lines = new List<DiffLine>() };
        int origIndex = 0, modIndex = 0, lcsIndex = 0;

        while (origIndex < originalLines.Length || modIndex < modifiedLines.Length)
        {
            if (lcsIndex < lcs.Count &&
                origIndex < originalLines.Length &&
                originalLines[origIndex] == lcs[lcsIndex])
            {
                if (modIndex < modifiedLines.Length && modifiedLines[modIndex] == lcs[lcsIndex])
                {
                    // Context line
                    hunk.Lines.Add(new DiffLine
                    {
                        Type = DiffLineType.Context,
                        Content = originalLines[origIndex],
                        OriginalLineNumber = origIndex + 1,
                        ModifiedLineNumber = modIndex + 1
                    });
                    origIndex++;
                    modIndex++;
                    lcsIndex++;
                }
                else
                {
                    // Added line
                    hunk.Lines.Add(new DiffLine
                    {
                        Type = DiffLineType.Added,
                        Content = modifiedLines[modIndex],
                        ModifiedLineNumber = modIndex + 1
                    });
                    modIndex++;
                }
            }
            else if (origIndex < originalLines.Length)
            {
                // Removed line
                hunk.Lines.Add(new DiffLine
                {
                    Type = DiffLineType.Removed,
                    Content = originalLines[origIndex],
                    OriginalLineNumber = origIndex + 1
                });
                origIndex++;
            }
            else if (modIndex < modifiedLines.Length)
            {
                // Added line
                hunk.Lines.Add(new DiffLine
                {
                    Type = DiffLineType.Added,
                    Content = modifiedLines[modIndex],
                    ModifiedLineNumber = modIndex + 1
                });
                modIndex++;
            }
        }

        if (hunk.Lines.Any())
        {
            hunk.OriginalStart = hunk.Lines.FirstOrDefault(l => l.OriginalLineNumber.HasValue)?.OriginalLineNumber ?? 1;
            hunk.ModifiedStart = hunk.Lines.FirstOrDefault(l => l.ModifiedLineNumber.HasValue)?.ModifiedLineNumber ?? 1;
            hunk.OriginalCount = hunk.Lines.Count(l => l.Type != DiffLineType.Added);
            hunk.ModifiedCount = hunk.Lines.Count(l => l.Type != DiffLineType.Removed);
            hunks.Add(hunk);
        }

        return hunks;
    }

    private List<string> ComputeLcs(string[] a, string[] b)
    {
        var m = a.Length;
        var n = b.Length;
        var dp = new int[m + 1, n + 1];

        for (int i = 1; i <= m; i++)
        {
            for (int j = 1; j <= n; j++)
            {
                if (a[i - 1] == b[j - 1])
                    dp[i, j] = dp[i - 1, j - 1] + 1;
                else
                    dp[i, j] = Math.Max(dp[i - 1, j], dp[i, j - 1]);
            }
        }

        var lcs = new List<string>();
        int x = m, y = n;

        while (x > 0 && y > 0)
        {
            if (a[x - 1] == b[y - 1])
            {
                lcs.Insert(0, a[x - 1]);
                x--;
                y--;
            }
            else if (dp[x - 1, y] > dp[x, y - 1])
            {
                x--;
            }
            else
            {
                y--;
            }
        }

        return lcs;
    }

    private string GenerateUnifiedDiff(string filePath, string original, string modified, List<DiffHunk> hunks)
    {
        var sb = new StringBuilder();
        var fileName = Path.GetFileName(filePath);

        sb.AppendLine($"--- a/{fileName}");
        sb.AppendLine($"+++ b/{fileName}");

        foreach (var hunk in hunks)
        {
            sb.AppendLine($"@@ -{hunk.OriginalStart},{hunk.OriginalCount} +{hunk.ModifiedStart},{hunk.ModifiedCount} @@");

            foreach (var line in hunk.Lines)
            {
                var prefix = line.Type switch
                {
                    DiffLineType.Added => "+",
                    DiffLineType.Removed => "-",
                    _ => " "
                };
                sb.AppendLine($"{prefix}{line.Content}");
            }
        }

        return sb.ToString();
    }

    public FilePatch? GetPendingPatch(string id) =>
        _pendingPatches.TryGetValue(id, out var patch) ? patch : null;

    public IEnumerable<FilePatch> GetAllPendingPatches() =>
        _pendingPatches.Values;

    public void AddPendingPatch(FilePatch patch) =>
        _pendingPatches[patch.Id] = patch;

    public void RemovePendingPatch(string id) =>
        _pendingPatches.Remove(id);

    public IEnumerable<FilePatch> GetAppliedPatchesHistory() =>
        _appliedPatchesHistory;

    public FilePatch? GetAppliedPatch(string id) =>
        _appliedPatchesHistory.FirstOrDefault(p => p.Id == id);
}

internal static class FileExtensions
{
    public static async Task CopyAsync(string sourceFile, string destinationFile, CancellationToken cancellationToken = default)
    {
        await using var sourceStream = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        await using var destinationStream = new FileStream(destinationFile, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true);
        await sourceStream.CopyToAsync(destinationStream, cancellationToken);
    }

    public static async Task CopyAsync(string sourceFile, string destinationFile, bool overwrite, CancellationToken cancellationToken = default)
    {
        var mode = overwrite ? FileMode.Create : FileMode.CreateNew;
        await using var sourceStream = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        await using var destinationStream = new FileStream(destinationFile, mode, FileAccess.Write, FileShare.None, 4096, true);
        await sourceStream.CopyToAsync(destinationStream, cancellationToken);
    }
}
