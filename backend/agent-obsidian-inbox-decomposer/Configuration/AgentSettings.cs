namespace TaskSandbox.Configuration;

/// <summary>All persistent user configuration for LocalCodingAgent.</summary>
public sealed class AgentSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; }
    public ObsidianSettings Obsidian { get; set; } = new();
    public WorkspaceSettings Workspaces { get; set; } = new();
    public ModelSettings Model { get; set; } = new();
    public PromptSettings Prompts { get; set; } = new();
    public RuntimeSettings Runtime { get; set; } = new();

    public static AgentSettings CreateDefault() => new() { SchemaVersion = CurrentSchemaVersion };
}

public sealed class ObsidianSettings
{
    /// <summary>Absolute path to the user's existing Obsidian vault. Empty until configured.</summary>
    public string VaultPath { get; set; } = string.Empty;
}

public sealed class WorkspaceSettings
{
    public string DefaultId { get; set; } = "main";
    public List<WorkspaceEntry> Registered { get; set; } = [];
}

public sealed class WorkspaceEntry
{
    public string Id { get; set; } = "main";
    public string Path { get; set; } = string.Empty;
}

public sealed class ModelSettings
{
    public string BaseUrl { get; set; } = "http://localhost:1234/";
    public string ModelId { get; set; } = "local-model";
    public int RequestTimeoutSeconds { get; set; } = 300;
}

public sealed class PromptSettings
{
    /// <summary>Optional custom directory. Empty means %LOCALAPPDATA%\LocalCodingAgent\Prompts.</summary>
    public string? DirectoryPath { get; set; }
}

public sealed class RuntimeSettings
{
    public int InboxPollIntervalSeconds { get; set; } = 5;
    public int MaxConcurrentTasks { get; set; } = 1;
    public int MaxCorrectionAttempts { get; set; } = 3;
    public int ProcessTimeoutSeconds { get; set; } = 600;
    public int MaxProcessOutputCharacters { get; set; } = 65536;
}
