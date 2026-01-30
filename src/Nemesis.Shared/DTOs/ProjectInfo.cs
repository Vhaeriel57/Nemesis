namespace Nemesis.Shared.DTOs;

public class ProjectInfo
{
    public string Path { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsIndexed { get; set; }
    public DateTime? LastIndexed { get; set; }
    public int TotalFiles { get; set; }
    public int TotalTypes { get; set; }
    public int TotalMethods { get; set; }
    public long TotalLinesOfCode { get; set; }
    public List<string> Errors { get; set; } = new();
}

public class IndexingProgress
{
    public string Phase { get; set; } = string.Empty;
    public int Current { get; set; }
    public int Total { get; set; }
    public string CurrentFile { get; set; } = string.Empty;
    public double PercentComplete => Total > 0 ? (double)Current / Total * 100 : 0;
}
