namespace Nemesis.Shared.DTOs;

public class LogEntry
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public LogLevel Level { get; set; }
    public string Source { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? Details { get; set; }
    public string? StackTrace { get; set; }
}

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
    Critical
}
