using TaskSandbox.Obsidian;

namespace TaskSandbox.Prompting;

/// <summary>Ephemeral level-two context for exactly one Obsidian task.</summary>
public sealed record TaskPromptContext(
    string TaskId,
    string WorkspaceId,
    string Description,
    IReadOnlyList<string> TargetFiles,
    IReadOnlyList<string> Requirements,
    IReadOnlyList<string> AcceptanceCriteria,
    string CurrentProgress,
    string CompletedWork,
    DependencyPolicy DependencyPolicy = global::TaskSandbox.Obsidian.DependencyPolicy.ExistingOnly);
