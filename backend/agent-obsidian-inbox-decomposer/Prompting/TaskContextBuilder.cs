using TaskSandbox.Obsidian;
using TaskSandbox.Tasks;

namespace TaskSandbox.Prompting;

/// <summary>Converts stored task documents into domain tasks and prepares isolated planning/execution contexts.</summary>
public sealed class TaskContextBuilder
{
    private readonly ObsidianTaskStore _taskStore;

    public TaskContextBuilder(ObsidianTaskStore taskStore, PromptSet prompts)
    {
        _taskStore = taskStore ?? throw new ArgumentNullException(nameof(taskStore));
        Prompts = prompts ?? throw new ArgumentNullException(nameof(prompts));
    }

    public PromptSet Prompts { get; }

    public UserTask CreateUserTask(ObsidianTaskDocument document, string? expectedWorkspaceId = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        string workspaceId = expectedWorkspaceId ?? document.WorkspaceId ?? string.Empty;
        if (string.IsNullOrWhiteSpace(workspaceId))
            throw new InvalidDataException($"У задачи '{document.Id}' не указан WorkspaceId.");

        if (!string.IsNullOrWhiteSpace(document.WorkspaceId) &&
            !string.Equals(document.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Задача '{document.Id}' назначена Workspace '{document.WorkspaceId}', ожидался '{workspaceId}'.");
        }

        return new UserTask(
            document.Id,
            document.Title,
            document.Body,
            workspaceId,
            document.ParentId,
            document.Priority,
            document.DependencyPolicy,
            document.DependsOn,
            document.TargetFiles,
            document.Requirements,
            document.AcceptanceCriteria,
            document.Status,
            document.SourcePath,
            document.ContentHash);
    }

    public async Task<TaskPlanningContext> BuildPlanningContextAsync(
        UserTask task,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        string memory = await LoadProjectMemoryAsync(task.WorkspaceId, cancellationToken).ConfigureAwait(false);
        return new TaskPlanningContext(task, memory);
    }

    public async Task<TaskExecutionContext> BuildExecutionContextAsync(
        UserTask task,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        string memory = await LoadProjectMemoryAsync(task.WorkspaceId, cancellationToken).ConfigureAwait(false);
        return new TaskExecutionContext(task, memory);
    }

    private Task<string> LoadProjectMemoryAsync(string workspaceId, CancellationToken cancellationToken)
    {
        if ((Prompts.Features & PromptFeatures.Memory) == 0)
            return Task.FromResult(string.Empty);

        return _taskStore.LoadProjectMemoryAsync(workspaceId, cancellationToken);
    }
}
