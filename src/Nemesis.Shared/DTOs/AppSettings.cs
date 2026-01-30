namespace Nemesis.Shared.DTOs;

public class AppSettings
{
    public LlmSettings Llm { get; set; } = new();
    public WebSearchSettings WebSearch { get; set; } = new();
    public ProjectSettings Project { get; set; } = new();
    public UiSettings Ui { get; set; } = new();
}

public class LlmSettings
{
    public string Provider { get; set; } = "ollama";
    public string OllamaBaseUrl { get; set; } = "http://localhost:11434";
    public string ModelName { get; set; } = "deepseek-coder-v2:16b";
    public string EmbeddingModel { get; set; } = "nomic-embed-text";
    public double Temperature { get; set; } = 0.7;
    public int MaxTokens { get; set; } = 4096;
    public int ContextWindow { get; set; } = 16384;

    public string? OpenAiBaseUrl { get; set; }
    public string? OpenAiApiKey { get; set; }
    public bool UseOpenAiFallback { get; set; } = false;
}

public class WebSearchSettings
{
    public bool Enabled { get; set; } = true;
    public string SearchEngine { get; set; } = "duckduckgo";
    public int MaxResults { get; set; } = 10;
    public int CacheExpirationHours { get; set; } = 24;
    public string CachePath { get; set; } = "./cache/web";
    public List<string> AllowedDomains { get; set; } = new()
    {
        "docs.unity3d.com",
        "forum.unity.com",
        "github.com",
        "stackoverflow.com",
        "learn.microsoft.com"
    };
}

public class ProjectSettings
{
    public List<string> ExcludedFolders { get; set; } = new()
    {
        "Library",
        "Temp",
        "obj",
        "bin",
        "Logs",
        ".git",
        ".vs",
        "Builds",
        "Build",
        "UserSettings"
    };
    public List<string> ExcludedExtensions { get; set; } = new()
    {
        ".meta",
        ".asset",
        ".prefab",
        ".unity",
        ".mat",
        ".shader"
    };
    public string BackupPath { get; set; } = "./backups";
    public bool AutoBackup { get; set; } = true;
    public int MaxBackupAge { get; set; } = 7;
}

public class UiSettings
{
    public bool AutoApplyPatches { get; set; } = false;
    public bool ShowDetailedLogs { get; set; } = true;
    public string Theme { get; set; } = "dark";
    public int MaxChatHistory { get; set; } = 100;
}
