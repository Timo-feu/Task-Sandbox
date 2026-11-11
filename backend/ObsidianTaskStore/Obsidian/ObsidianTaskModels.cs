namespace TaskSandbox.Obsidian;

public enum ObsidianTaskPriority : byte
{
    Low,
    Normal,
    High,
    Critical
}

public enum ObsidianTaskState : byte
{
    Inbox,
    Planning,
    Queue,
    InProgress,
    Completed,
    Failed
}

/// <summary>Configuration for the LocalCodingAgent folder inside an existing Obsidian vault.</summary>
public sealed record ObsidianTaskStoreOptions
{
    public required string VaultPath { get; init; }
    public string AgentFolderName { get; init; } = "LocalCodingAgent";
    public int MaxMarkdownFileBytes { get; init; } = 1_048_576;
}

/// <summary>A Markdown task read from the Obsidian vault.</summary>
public sealed record ObsidianTaskDocument(
    string Id,
    string Title,
    string Body,
    string? WorkspaceId,
    string? ParentId,
    ObsidianTaskPriority Priority,
    IReadOnlyList<string> DependsOn,
    IReadOnlyList<string> TargetFiles,
    IReadOnlyList<string> Requirements,
    IReadOnlyList<string> AcceptanceCriteria,
    string Status,
    string SourcePath);

/// <summary>One small task produced by the Task Decomposer.</summary>
public sealed record ObsidianSubtaskDefinition(
    string Id,
    string Title,
    string Description,
    string WorkspaceId,
    IReadOnlyList<string> DependsOn,
    IReadOnlyList<string> TargetFiles,
    IReadOnlyList<string> Requirements,
    IReadOnlyList<string> AcceptanceCriteria,
    ObsidianTaskPriority Priority = ObsidianTaskPriority.Normal);

/// <summary>Validated plan and its generated subtasks.</summary>
public sealed record ObsidianTaskPlan(
    string ParentTaskId,
    string Title,
    string PlanMarkdown,
    IReadOnlyList<ObsidianSubtaskDefinition> Subtasks);
