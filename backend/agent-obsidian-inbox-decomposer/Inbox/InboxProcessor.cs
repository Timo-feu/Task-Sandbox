using TaskSandbox.Obsidian;
using TaskSandbox.Planning;

namespace TaskSandbox.Inbox;

public enum InboxProcessStatus : byte
{
    NoWork,
    PublishedPlan,
    PlanningFailed
}

public sealed record InboxProcessorOptions
{
    public required string WorkspaceId { get; init; }
    public required string WorkspacePath { get; init; }
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>Moves one Inbox request into planning and asks the independent Decomposer to publish subtasks.</summary>
public sealed class InboxProcessor
{
    private readonly ObsidianTaskStore _taskStore;
    private readonly TaskDecomposer _decomposer;
    private readonly InboxProcessorOptions _options;

    public InboxProcessor(
        ObsidianTaskStore taskStore,
        TaskDecomposer decomposer,
        InboxProcessorOptions options)
    {
        _taskStore = taskStore ?? throw new ArgumentNullException(nameof(taskStore));
        _decomposer = decomposer ?? throw new ArgumentNullException(nameof(decomposer));
        _options = options ?? throw new ArgumentNullException(nameof(options));

        if (string.IsNullOrWhiteSpace(options.WorkspaceId))
            throw new ArgumentException("WorkspaceId must be configured.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.WorkspacePath))
            throw new ArgumentException("WorkspacePath must be configured.", nameof(options));
        if (options.PollInterval < TimeSpan.FromSeconds(1) || options.PollInterval > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(options), "PollInterval must be between 1 second and 5 minutes.");
    }

    /// <summary>Processes at most one parent task. Safe to call periodically.</summary>
    public async Task<InboxProcessStatus> ProcessOneAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ObsidianTaskDocument> planningTasks = await _taskStore
            .ReadPlanningTasksAsync(cancellationToken).ConfigureAwait(false);

        foreach (ObsidianTaskDocument task in planningTasks)
        {
            if (!MatchesWorkspace(task))
            {
                Console.WriteLine(
                    $"[Inbox] Задача '{task.Id}' пропущена: Workspace '{task.WorkspaceId}' не совпадает с '{_options.WorkspaceId}'.");
                continue;
            }

            return await PlanOneAsync(task, cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<ObsidianTaskDocument> inboxTasks = await _taskStore
            .ReadInboxAsync(cancellationToken).ConfigureAwait(false);

        foreach (ObsidianTaskDocument inboxTask in inboxTasks)
        {
            if (!MatchesWorkspace(inboxTask))
            {
                Console.WriteLine(
                    $"[Inbox] Заметка '{Path.GetFileName(inboxTask.SourcePath)}' пропущена: Workspace '{inboxTask.WorkspaceId}' не совпадает с '{_options.WorkspaceId}'.");
                continue;
            }

            ObsidianTaskDocument planningTask = await _taskStore.MoveInboxTaskToPlanningAsync(
                inboxTask.SourcePath,
                cancellationToken).ConfigureAwait(false);

            Console.WriteLine($"[Inbox] Принята большая задача '{planningTask.Id}': {planningTask.Title}");
            return await PlanOneAsync(planningTask, cancellationToken).ConfigureAwait(false);
        }

        return InboxProcessStatus.NoWork;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        _taskStore.EnsureInitialized();
        Console.WriteLine($"[Inbox] Наблюдение за {_taskStore.AgentRootPath} каждые {_options.PollInterval.TotalSeconds:0} сек.");
        Console.WriteLine($"[Workspace] {_options.WorkspaceId} → {_options.WorkspacePath}");

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InboxProcessStatus status = await ProcessOneAsync(cancellationToken).ConfigureAwait(false);
            if (status is InboxProcessStatus.NoWork or InboxProcessStatus.PlanningFailed)
                await Task.Delay(_options.PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<InboxProcessStatus> PlanOneAsync(
        ObsidianTaskDocument task,
        CancellationToken cancellationToken)
    {
        try
        {
            TaskDecompositionResult result = await _decomposer.DecomposeAndPublishAsync(
                task,
                _options.WorkspaceId,
                cancellationToken).ConfigureAwait(false);

            Console.WriteLine(
                $"[Decomposer] План '{result.ParentTaskId}' опубликован: {result.Title}; подзадач: {result.SubtaskCount}.");
            return InboxProcessStatus.PublishedPlan;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverablePlanningError(exception))
        {
            Console.Error.WriteLine($"[Decomposer] Не удалось обработать '{task.Id}': {exception.Message}");
            await _taskStore.SetPlanningErrorAsync(
                task.Id,
                exception.Message,
                CancellationToken.None).ConfigureAwait(false);
            return InboxProcessStatus.PlanningFailed;
        }
    }

    private bool MatchesWorkspace(ObsidianTaskDocument task) =>
        string.IsNullOrWhiteSpace(task.WorkspaceId) ||
        string.Equals(task.WorkspaceId, _options.WorkspaceId, StringComparison.OrdinalIgnoreCase);

    private static bool IsRecoverablePlanningError(Exception exception) => exception is
        HttpRequestException or
        TimeoutException or
        System.Text.Json.JsonException or
        InvalidDataException or
        IOException or
        UnauthorizedAccessException;
}
