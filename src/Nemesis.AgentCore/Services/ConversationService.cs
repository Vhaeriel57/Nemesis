using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nemesis.Shared.DTOs;

namespace Nemesis.AgentCore.Services;

/// <summary>
/// Service for persisting and retrieving conversation sessions
/// </summary>
public class ConversationService
{
    private readonly string _storagePath;
    private readonly ILogger<ConversationService> _logger;
    private readonly Dictionary<string, AgentConversation> _activeConversations = new();
    private readonly object _lock = new();

    public event Action<AgentConversation>? ConversationUpdated;

    public ConversationService(string storagePath, ILogger<ConversationService> logger)
    {
        _storagePath = storagePath;
        _logger = logger;
        Directory.CreateDirectory(_storagePath);
    }

    public AgentConversation CreateConversation(string? title = null, string? projectPath = null)
    {
        var conversation = new AgentConversation
        {
            Title = title ?? $"Conversation {DateTime.Now:yyyy-MM-dd HH:mm}",
            ProjectPath = projectPath
        };

        lock (_lock)
        {
            _activeConversations[conversation.Id] = conversation;
        }

        _logger.LogInformation("Created conversation: {Id}", conversation.Id);
        return conversation;
    }

    public AgentConversation? GetConversation(string conversationId)
    {
        lock (_lock)
        {
            if (_activeConversations.TryGetValue(conversationId, out var conversation))
                return conversation;
        }

        // Try to load from disk
        return LoadConversation(conversationId);
    }

    public void AddMessage(string conversationId, AgentMessage message)
    {
        var conversation = GetConversation(conversationId);
        if (conversation == null)
        {
            _logger.LogWarning("Conversation not found: {Id}", conversationId);
            return;
        }

        lock (_lock)
        {
            conversation.Messages.Add(message);
            conversation.UpdatedAt = DateTime.UtcNow;
        }

        ConversationUpdated?.Invoke(conversation);

        // Auto-save periodically
        if (conversation.Messages.Count % 5 == 0)
        {
            _ = Task.Run(() => SaveConversation(conversation));
        }
    }

    public List<AgentMessage> GetMessages(string conversationId)
    {
        var conversation = GetConversation(conversationId);
        return conversation?.Messages ?? new List<AgentMessage>();
    }

    public async Task SaveConversation(AgentConversation conversation)
    {
        try
        {
            var filePath = GetConversationFilePath(conversation.Id);
            var json = JsonSerializer.Serialize(conversation, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            await File.WriteAllTextAsync(filePath, json);
            _logger.LogDebug("Saved conversation: {Id}", conversation.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save conversation: {Id}", conversation.Id);
        }
    }

    public AgentConversation? LoadConversation(string conversationId)
    {
        try
        {
            var filePath = GetConversationFilePath(conversationId);
            if (!File.Exists(filePath))
                return null;

            var json = File.ReadAllText(filePath);
            var conversation = JsonSerializer.Deserialize<AgentConversation>(json);

            if (conversation != null)
            {
                lock (_lock)
                {
                    _activeConversations[conversation.Id] = conversation;
                }
            }

            return conversation;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load conversation: {Id}", conversationId);
            return null;
        }
    }

    public List<ConversationSession> GetAllSessions()
    {
        var sessions = new List<ConversationSession>();

        try
        {
            var files = Directory.GetFiles(_storagePath, "*.json");
            foreach (var file in files)
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var conversation = JsonSerializer.Deserialize<AgentConversation>(json);
                    if (conversation != null)
                    {
                        sessions.Add(new ConversationSession
                        {
                            Id = conversation.Id,
                            Name = conversation.Title,
                            CreatedAt = conversation.CreatedAt,
                            LastAccessedAt = conversation.UpdatedAt,
                            ProjectPath = conversation.ProjectPath,
                            MessageCount = conversation.Messages.Count,
                            Summary = conversation.Messages.FirstOrDefault()?.Content?.Substring(0, Math.Min(100, conversation.Messages.FirstOrDefault()?.Content?.Length ?? 0))
                        });
                    }
                }
                catch { /* Skip invalid files */ }
            }

            // Add active conversations not yet saved
            lock (_lock)
            {
                foreach (var conv in _activeConversations.Values)
                {
                    if (!sessions.Any(s => s.Id == conv.Id))
                    {
                        sessions.Add(new ConversationSession
                        {
                            Id = conv.Id,
                            Name = conv.Title,
                            CreatedAt = conv.CreatedAt,
                            LastAccessedAt = conv.UpdatedAt,
                            ProjectPath = conv.ProjectPath,
                            MessageCount = conv.Messages.Count
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list sessions");
        }

        return sessions.OrderByDescending(s => s.LastAccessedAt).ToList();
    }

    public async Task DeleteConversation(string conversationId)
    {
        lock (_lock)
        {
            _activeConversations.Remove(conversationId);
        }

        var filePath = GetConversationFilePath(conversationId);
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }

        _logger.LogInformation("Deleted conversation: {Id}", conversationId);
    }

    public async Task SaveAllAsync()
    {
        List<AgentConversation> toSave;
        lock (_lock)
        {
            toSave = _activeConversations.Values.ToList();
        }

        foreach (var conversation in toSave)
        {
            await SaveConversation(conversation);
        }
    }

    private string GetConversationFilePath(string conversationId)
    {
        return Path.Combine(_storagePath, $"{conversationId}.json");
    }
}
