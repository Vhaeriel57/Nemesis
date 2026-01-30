using Nemesis.Shared.Models;

namespace Nemesis.Shared.DTOs;

public class FilePatch
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string FilePath { get; set; } = string.Empty;
    public string OriginalContent { get; set; } = string.Empty;
    public string ModifiedContent { get; set; } = string.Empty;
    public string UnifiedDiff { get; set; } = string.Empty;
    public PatchStatus Status { get; set; } = PatchStatus.Pending;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? BackupPath { get; set; }
    public string? ErrorMessage { get; set; }
    public List<DiffHunk> Hunks { get; set; } = new();
}

public class DiffHunk
{
    public int OriginalStart { get; set; }
    public int OriginalCount { get; set; }
    public int ModifiedStart { get; set; }
    public int ModifiedCount { get; set; }
    public List<DiffLine> Lines { get; set; } = new();
}

public class DiffLine
{
    public DiffLineType Type { get; set; }
    public string Content { get; set; } = string.Empty;
    public int? OriginalLineNumber { get; set; }
    public int? ModifiedLineNumber { get; set; }
}

public enum DiffLineType
{
    Context,
    Added,
    Removed
}

public class PatchSet
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Description { get; set; } = string.Empty;
    public List<FilePatch> Patches { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? GitBranch { get; set; }
    public bool IsApplied { get; set; }
}
