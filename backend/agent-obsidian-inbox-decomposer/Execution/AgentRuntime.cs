using TaskSandbox.Inbox;
using TaskSandbox.Obsidian;

namespace TaskSandbox.Execution;

/// <summary>Coordinates Inbox planning and Queue execution in one sequential runtime loop.</summary>
public sealed class AgentRuntime
{
    private readonly ObsidianTaskStore _taskStore;
    private readonly InboxProcessor _inboxProcessor;
    private readonly QueueWorker _queueWorker;
    private readonly TimeSpan _pollInterval;
    private readonly RuntimeEventJournal? _journal;

    public AgentRuntime(
        ObsidianTaskStore taskStore,
        InboxProcessor inboxProcessor,
        QueueWorker queueWorker,
        TimeSpan pollInterval,
        RuntimeEventJournal? journal = null)
    {
        _taskStore = taskStore ?? throw new ArgumentNullException(nameof(taskStore));
        _inboxProcessor = inboxProcessor ?? throw new ArgumentNullException(nameof(inboxProcessor));
        _queueWorker = queueWorker ?? throw new ArgumentNullException(nameof(queueWorker));
        _journal = journal;
        if (pollInterval < TimeSpan.FromSeconds(1) || pollInterval > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        _pollInterval = pollInterval;
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        _taskStore.EnsureInitialized();
        int recovered = await _taskStore.RecoverInProgressTasksAsync(cancellationToken).ConfigureAwait(false);
        if (recovered > 0)
            Console.WriteLine($"[Queue] Восстановлено задач после перезапуска: {recovered}.");

        while (true)
        {
            InboxProcessStatus status = await _inboxProcessor.ProcessOneAsync(cancellationToken).ConfigureAwait(false);
            if (status != InboxProcessStatus.PublishedPlan)
                break;
        }

        await _queueWorker.ProcessAvailableAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        Console.WriteLine("[Agent] Inbox + Queue runtime запущен.");
        bool initialized = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (!initialized)
                {
                    _taskStore.EnsureInitialized();
                    int recovered = await _taskStore.RecoverInProgressTasksAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (recovered > 0)
                        Console.WriteLine($"[Queue] Восстановлено задач после перезапуска: {recovered}.");
                    initialized = true;
                    if (_journal is not null)
                        await _journal.WriteAsync("runtime_ready", "Исполнитель готов",
                            "Inbox и Queue worker запущены", cancellationToken: CancellationToken.None)
                            .ConfigureAwait(false);
                }

                InboxProcessStatus inboxStatus = await _inboxProcessor
                    .ProcessOneAsync(cancellationToken).ConfigureAwait(false);
                int executed = await _queueWorker
                    .ProcessAvailableAsync(cancellationToken).ConfigureAwait(false);

                if (inboxStatus != InboxProcessStatus.PublishedPlan && executed == 0)
                    await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"[Runtime] Ошибка рабочего цикла: {exception.GetType().Name}: {exception.Message}");
                if (_journal is not null)
                    await _journal.WriteAsync("runtime_cycle_error", "Рабочий цикл остановился с ошибкой",
                        $"{exception.GetType().Name}: {exception.Message}", severity: "error",
                        cancellationToken: CancellationToken.None).ConfigureAwait(false);

                // A transient Obsidian I/O or provider failure must not silently kill
                // the worker while the dashboard remains online.
                await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
                initialized = false;
            }
        }
    }
}
