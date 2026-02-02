using Nemesis.Shared.DTOs;

namespace Nemesis.Shared.Interfaces;

public interface IPatchService
{
    void AddPendingPatch(FilePatch patch);

    FilePatch CreatePatch(string filePath, string originalContent, string modifiedContent);
    PatchSet CreatePatchSet(string description, List<(string filePath, string original, string modified)> changes);

    Task<bool> ApplyPatchAsync(
        FilePatch patch,
        bool createBackup = true,
        CancellationToken cancellationToken = default);

    Task<bool> ApplyPatchSetAsync(
        PatchSet patchSet,
        bool createBackup = true,
        CancellationToken cancellationToken = default);

    Task<bool> RollbackPatchAsync(
        FilePatch patch,
        CancellationToken cancellationToken = default);

    Task<bool> RollbackPatchSetAsync(
        PatchSet patchSet,
        CancellationToken cancellationToken = default);

    Task<string> CreateBackupAsync(
        string filePath,
        CancellationToken cancellationToken = default);

    Task<bool> RestoreBackupAsync(
        string backupPath,
        string originalPath,
        CancellationToken cancellationToken = default);

    Task CleanupOldBackupsAsync(
        int maxAgeDays,
        CancellationToken cancellationToken = default);
}
