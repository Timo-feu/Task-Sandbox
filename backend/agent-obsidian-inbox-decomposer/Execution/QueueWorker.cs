using TaskSandbox.Obsidian;

namespace TaskSandbox.Execution;

public sealed record QueueWorkerOptions
{
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>Single-consumer worker. Queue claiming and crash recovery are owned by ObsidianTaskStore.</summary>
public sealed class QueueWorker
{
    private readonly ObsidianTaskStore _taskStore;
    private readonly TaskExecutor _executor;
    private readonly QueueWorkerOptions _options;

    public QueueWorker(ObsidianTaskStore taskStore, TaskExecutor executor, QueueWorkerOptions options)
    {
        _taskStore = taskStore ?? throw new ArgumentNullException(nameof(taskStore));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _options = options ?? throw new ArgumentNullException(nameof(options));
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
            try
            {
                TaskExecutionResult result = await _executor.ExecuteAsync(task, cancellationToken)
                    .ConfigureAwait(false);
                if (result.Success)
                {
                    await _taskStore.CompleteTaskAsync(task.Id, result.ReportMarkdown, cancellationToken)
                        .ConfigureAwait(false);
                    Console.WriteLine($"[Executor] Задача '{task.Id}' завершена. Вызовов инструментов: {result.ToolCalls}.");
                }
                else
                {
                    await _taskStore.FailTaskAsync(task.Id, result.ReportMarkdown, cancellationToken)
                        .ConfigureAwait(false);
                    Console.WriteLine($"[Executor] Задача '{task.Id}' остановлена моделью как невыполнимая.");
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
