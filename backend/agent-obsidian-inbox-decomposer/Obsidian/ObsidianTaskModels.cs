namespace TaskSandbox.Obsidian;

public enum ObsidianTaskPriority : byte
{
    Low,
    Normal,
    High,
    Critical
}

public enum DependencyPolicy : byte
{
    /// <summary>Use only dependencies already declared by the project.</summary>
    ExistingOnly,
    /// <summary>New dependencies may be added only when technically justified by the task.</summary>
    AllowIfNecessary,
    /// <summary>New dependencies may be added when they are the best technical choice.</summary>
    Unrestricted
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
    DependencyPolicy DependencyPolicy,
    IReadOnlyList<string> DependsOn,
    IReadOnlyList<string> TargetFiles,
    IReadOnlyList<string> Requirements,
    IReadOnlyList<string> AcceptanceCriteria,
    string Status,
    string SourcePath,
    string ContentHash);

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
    DependencyPolicy DependencyPolicy = global::TaskSandbox.Obsidian.DependencyPolicy.ExistingOnly,
    ObsidianTaskPriority Priority = ObsidianTaskPriority.Normal);

/// <summary>Validated plan and its generated subtasks.</summary>
public sealed record ObsidianTaskPlan(
    string ParentTaskId,
    string Title,
    string PlanMarkdown,
    IReadOnlyList<ObsidianSubtaskDefinition> Subtasks,
    string OriginalContentHash);
