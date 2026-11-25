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

/// <summary>
/// Active model connection plus saved connection profiles. The flat properties are
/// retained for compatibility with existing runtime components and old settings files.
/// API keys are never stored here; only profile identifiers are persisted.
/// </summary>
public sealed class ModelSettings
{
    public string ActiveProfileId { get; set; } = "local";
    public List<ModelProfileSettings> Profiles { get; set; } = [];

    // Active-profile projection kept for source and settings compatibility.
    public string ProviderId { get; set; } = "lm-studio";
    public string BaseUrl { get; set; } = "http://localhost:1234/";
    public string ModelId { get; set; } = "local-model";
    public int RequestTimeoutSeconds { get; set; } = 300;
}

public sealed class ModelProfileSettings
{
    public string Id { get; set; } = "local";
    public string Name { get; set; } = "Local model";
    public string ProviderId { get; set; } = "lm-studio";
    public string BaseUrl { get; set; } = "http://localhost:1234/";
    public string ModelId { get; set; } = "local-model";
    public int RequestTimeoutSeconds { get; set; } = 300;
}

public sealed class PromptSettings
{
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
