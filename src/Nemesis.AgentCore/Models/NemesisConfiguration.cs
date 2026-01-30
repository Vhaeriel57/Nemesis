using Nemesis.Shared.DTOs;

namespace Nemesis.AgentCore.Models;

public class NemesisConfiguration
{
    public LlmSettings Llm { get; set; } = new();
    public WebSearchSettings WebSearch { get; set; } = new();
    public ProjectSettings Project { get; set; } = new();
    public UiSettings Ui { get; set; } = new();
    public StorageSettings Storage { get; set; } = new();
}

public class StorageSettings
{
    public string DatabasePath { get; set; } = "./data/nemesis.db";
    public string VectorStorePath { get; set; } = "./data/vectors.db";
    public string CachePath { get; set; } = "./cache";
    public string BackupPath { get; set; } = "./backups";
}
