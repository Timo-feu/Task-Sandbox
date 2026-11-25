using TaskSandbox.Obsidian;

namespace TaskSandbox.Tasks;

/// <summary>
/// Domain representation of the user's original task, independent of its Obsidian Markdown storage.
/// It contains task data only; execution, persistence and prompt construction belong to other services.
/// </summary>
public sealed record UserTask(
    string Id,
    string Title,
    string Description,
    string WorkspaceId,
    string? ParentId,
    ObsidianTaskPriority Priority,
    DependencyPolicy DependencyPolicy,
    IReadOnlyList<string> DependsOn,
    IReadOnlyList<string> TargetFiles,
    IReadOnlyList<string> Requirements,
    IReadOnlyList<string> AcceptanceCriteria,
    string Status,
    string SourcePath,
    string ContentHash)
{
    public string CurrentProgress { get; init; } = "Задача ещё не начата.";
    public string CompletedWork { get; init; } = "В рамках текущей задачи пока ничего не выполнено.";
}
