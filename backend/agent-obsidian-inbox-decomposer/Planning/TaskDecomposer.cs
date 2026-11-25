using TaskSandbox;
using System.Text;
using TaskSandbox.Obsidian;
using TaskSandbox.Prompting;

namespace TaskSandbox.Planning;

public sealed record TaskDecompositionResult(
    string ParentTaskId,
    string Title,
    int SubtaskCount);

/// <summary>Runs one isolated planning request and publishes its validated plan to the Obsidian queue.</summary>
public sealed class TaskDecomposer
{
    private const int MaxResponseCharacters = 128 * 1024;

    private readonly IModelProvider _modelProvider;
    private readonly ObsidianTaskStore _taskStore;
    private readonly TaskContextBuilder _contextBuilder;

    public TaskDecomposer(
        IModelProvider modelProvider,
        ObsidianTaskStore taskStore,
        TaskContextBuilder contextBuilder)
    {
        _modelProvider = modelProvider ?? throw new ArgumentNullException(nameof(modelProvider));
        _taskStore = taskStore ?? throw new ArgumentNullException(nameof(taskStore));
        _contextBuilder = contextBuilder ?? throw new ArgumentNullException(nameof(contextBuilder));
    }

    public async Task<TaskDecompositionResult> DecomposeAndPublishAsync(
        ObsidianTaskDocument originalTask,
        string workspaceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(originalTask);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        ObsidianTaskDocument task = await _taskStore.SetPlanningWorkspaceAsync(
            originalTask.Id,
            workspaceId,
            cancellationToken).ConfigureAwait(false);

        var userTask = _contextBuilder.CreateUserTask(task, workspaceId);
        TaskPlanningContext context = await _contextBuilder
            .BuildPlanningContextAsync(userTask, cancellationToken)
            .ConfigureAwait(false);

        ModelMessage[] messages = TaskPlanningPromptBuilder.BuildMessages(_contextBuilder.Prompts, context);
        string response = await CompleteBoundedAsync(
            new ModelRequest(messages, Temperature: 0.2f),
            cancellationToken).ConfigureAwait(false);

        ObsidianTaskPlan plan = TaskPlanParser.Parse(
            response, userTask.Id, userTask.WorkspaceId, userTask.ContentHash, userTask.DependencyPolicy);
        await _taskStore.PublishPlanAsync(plan, cancellationToken).ConfigureAwait(false);

        return new TaskDecompositionResult(plan.ParentTaskId, plan.Title, plan.Subtasks.Count);
    }

    private async Task<string> CompleteBoundedAsync(
        ModelRequest request,
        CancellationToken cancellationToken)
    {
        var result = new StringBuilder(4096);

        await _modelProvider.StreamAsync(
            request,
            content =>
            {
                if (result.Length + content.Length > MaxResponseCharacters)
                    throw new InvalidDataException(
                        $"Ответ Decomposer превысил лимит {MaxResponseCharacters} символов.");

                result.Append(content);
                return ValueTask.CompletedTask;
            },
            cancellationToken).ConfigureAwait(false);

        if (result.Length == 0)
            throw new InvalidDataException("Decomposer вернул пустой ответ.");

        return result.ToString();
    }
}
