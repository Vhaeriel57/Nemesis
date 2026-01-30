using Nemesis.Shared.DTOs;
using NemesisLogLevel = Nemesis.Shared.DTOs.LogLevel;

namespace Nemesis.Server.Services;

public class LogService
{
    private readonly List<LogEntry> _logs = new();
    private readonly object _lock = new();
    private const int MaxLogs = 1000;

    public event Action<LogEntry>? LogAdded;

    public void AddLog(NemesisLogLevel level, string source, string message, string? details = null)
    {
        var entry = new LogEntry
        {
            Timestamp = DateTime.UtcNow,
            Level = level,
            Source = source,
            Message = message,
            Details = details
        };

        lock (_lock)
        {
            _logs.Add(entry);

            while (_logs.Count > MaxLogs)
            {
                _logs.RemoveAt(0);
            }
        }

        LogAdded?.Invoke(entry);
    }

    public List<LogEntry> GetRecentLogs(int count = 100)
    {
        lock (_lock)
        {
            return _logs.TakeLast(count).ToList();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _logs.Clear();
        }
    }
}
