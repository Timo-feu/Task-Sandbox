using TaskSandbox.Obsidian;
using TaskSandbox.Prompting;

namespace TaskSandbox.Execution;

public sealed record QueueWorkerOptions
{
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>Single-consumer worker. Queue claiming and crash recovery are owned by ObsidianTaskStore.</summary>
public sealed class QueueWorker
{
    private readonly ObsidianTaskStore _taskStore;
    private readonly SelfCorrectionEngine _executionPipeline;
    private readonly QueueWorkerOptions _options;
    private readonly ProjectMemoryUpdater? _memoryUpdater;
    private readonly RuntimeEventJournal? _journal;

    public QueueWorker(ObsidianTaskStore taskStore, SelfCorrectionEngine executionPipeline, QueueWorkerOptions options, ProjectMemoryUpdater? memoryUpdater = null, RuntimeEventJournal? journal = null)
    {
        _taskStore = taskStore ?? throw new ArgumentNullException(nameof(taskStore));
        _executionPipeline = executionPipeline ?? throw new ArgumentNullException(nameof(executionPipeline));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _memoryUpdater = memoryUpdater;
        _journal = journal;
        if (options.PollInterval < TimeSpan.FromSeconds(1) || options.PollInterval > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(options));
    }

    public async Task<int> ProcessAvailableAsync(CancellationToken cancellationToken = default)
    {
        int processed = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObsidianTaskDocument? task = await _taskStore
                .ClaimNextReadyTaskAsync(cancellationToken).ConfigureAwait(false);
            if (task is null)
                return processed;

            Console.WriteLine($"[Executor] Начата задача '{task.Id}': {task.Title}");
            if (_journal is not null) await _journal.WriteAsync("task_started", "Задача взята в работу", task.Title, task.Id, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            try
            {
                TaskExecutionResult result = await _executionPipeline.ExecuteAsync(task, cancellationToken)
                    .ConfigureAwait(false);
                if (result.Success)
                {
                    if (_memoryUpdater is not null)
                    {
                        try
                        {
                            bool updated = await _memoryUpdater.UpdateAsync(task, result.ReportMarkdown, cancellationToken)
                                .ConfigureAwait(false);
                            Console.WriteLine(updated
                                ? $"[Memory] Project Memory обновлена после задачи '{task.Id}'."
                                : $"[Memory] Задача '{task.Id}' уже учтена в Project Memory.");
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception exception)
                        {
                            Console.Error.WriteLine($"[Memory] Не удалось обновить память после задачи '{task.Id}': {exception.Message}");
                        }
                    }
                    await _taskStore.CompleteTaskAsync(task.Id, result.ReportMarkdown, cancellationToken)
                        .ConfigureAwait(false);
                    Console.WriteLine($"[Executor] Задача '{task.Id}' завершена. Вызовов инструментов: {result.ToolCalls}.");
                    if (_journal is not null) await _journal.WriteAsync("task_completed", "Задача завершена", $"Вызовов инструментов: {result.ToolCalls}", task.Id, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    await _taskStore.FailTaskAsync(task.Id, result.ReportMarkdown, cancellationToken)
                        .ConfigureAwait(false);
                    Console.WriteLine($"[Executor] Задача '{task.Id}' остановлена моделью как невыполнимая.");
                    if (_journal is not null) await _journal.WriteAsync("task_failed", "Задача не выполнена", "Модель вернула отрицательный результат или проверка не пройдена", task.Id, "error", CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                string report = $"# Ошибка выполнения\n\n{exception.GetType().Name}: {exception.Message}\n\n" +
                                "Задача перемещена в Failed. После анализа причины её можно вернуть в Queue.";
                await _taskStore.FailTaskAsync(task.Id, report, CancellationToken.None).ConfigureAwait(false);
                Console.Error.WriteLine($"[Executor] Задача '{task.Id}' завершилась ошибкой: {exception.Message}");
                if (_journal is not null) await _journal.WriteAsync("task_error", "Ошибка выполнения задачи", exception.Message, task.Id, "error", CancellationToken.None).ConfigureAwait(false);
            }
            processed++;
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        _taskStore.EnsureInitialized();
        int recovered = await _taskStore.RecoverInProgressTasksAsync(cancellationToken).ConfigureAwait(false);
        if (recovered > 0)
            Console.WriteLine($"[Queue] Восстановлено задач после перезапуска: {recovered}.");

        Console.WriteLine($"[Queue] Worker запущен. Интервал опроса: {_options.PollInterval.TotalSeconds:0} сек.");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int processed = await ProcessAvailableAsync(cancellationToken).ConfigureAwait(false);
            if (processed == 0)
                await Task.Delay(_options.PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }
}
