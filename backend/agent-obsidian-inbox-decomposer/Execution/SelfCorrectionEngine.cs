using System.Text;
using TaskSandbox.Obsidian;

namespace TaskSandbox.Execution;

/// <summary>Runs execution and deterministic validation, then gives bounded feedback-driven retries.</summary>
public sealed class SelfCorrectionEngine
{
    private readonly TaskExecutor _executor;
    private readonly ResultValidator _validator;
    private readonly int _maxCorrectionAttempts;
    private readonly WorkspaceRecoveryStore _recoveryStore;
    private readonly RuntimeEventJournal? _journal;

    public SelfCorrectionEngine(TaskExecutor executor, ResultValidator validator, int maxCorrectionAttempts,
        WorkspaceRecoveryStore recoveryStore, RuntimeEventJournal? journal = null)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _recoveryStore = recoveryStore ?? throw new ArgumentNullException(nameof(recoveryStore));
        _journal = journal;
        if (maxCorrectionAttempts is < 0 or > 10)
            throw new ArgumentOutOfRangeException(nameof(maxCorrectionAttempts));
        _maxCorrectionAttempts = maxCorrectionAttempts;
    }

    public async Task<TaskExecutionResult> ExecuteAsync(
        ObsidianTaskDocument task,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        FileRecoverySession recovery = _recoveryStore.BeginTask(task.Id);
        string? feedback = null;
        int totalToolCalls = 0;
        var allTrace = new List<TaskSandbox.Tools.TaskToolTrace>();
        TaskExecutionResult? lastResult = null;
        TaskValidationReport? lastValidation = null;

        try
        {
            for (int attempt = 0; attempt <= _maxCorrectionAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Console.WriteLine($"[Validator] Попытка {attempt + 1}/{_maxCorrectionAttempts + 1} для задачи '{task.Id}'.");
                if (_journal is not null) await _journal.WriteAsync("execution_attempt", "Начата попытка выполнения", $"Попытка {attempt + 1} из {_maxCorrectionAttempts + 1}", task.Id, cancellationToken: CancellationToken.None).ConfigureAwait(false);

                lastResult = await _executor.ExecuteAsync(task, cancellationToken, feedback, recovery).ConfigureAwait(false);
                totalToolCalls += lastResult.ToolCalls;
                allTrace.AddRange(lastResult.ToolTrace);
                lastValidation = _validator.Validate(task, lastResult);

                if (lastValidation.Success)
                {
                    if (_journal is not null) await _journal.WriteAsync("validation_passed", "Проверка пройдена", "Результат прошёл независимую проверку", task.Id, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                    recovery.Commit();
                    if (_journal is not null && attempt > 0) await _journal.WriteAsync("correction_succeeded", "Исправление успешно", $"Проверка пройдена после {attempt} попыток исправления", task.Id, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                    string report = AppendValidation(lastResult.ReportMarkdown, lastValidation, attempt, totalToolCalls);
                    return new TaskExecutionResult(task.Id, true, report, totalToolCalls, allTrace.ToArray());
                }

                if (_journal is not null) await _journal.WriteAsync("validation_failed", "Проверка не пройдена", string.Join("; ", lastValidation.Issues.Take(3)), task.Id, "warning", CancellationToken.None).ConfigureAwait(false);
                if (attempt == _maxCorrectionAttempts)
                    break;

                if (_journal is not null) await _journal.WriteAsync("correction_started", "Запущено исправление", $"Попытка исправления {attempt + 1}", task.Id, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                feedback = BuildFeedback(lastValidation, attempt + 1, _maxCorrectionAttempts);
            }

            string failureReport = AppendValidation(
                lastResult?.ReportMarkdown ?? "# Выполнение не завершено",
                lastValidation!, _maxCorrectionAttempts, totalToolCalls);
            recovery.Rollback();
            if (_journal is not null) await _journal.WriteAsync("task_rollback", "Изменения отменены", "Проверка не пройдена; Workspace восстановлен", task.Id, "warning", CancellationToken.None).ConfigureAwait(false);
            failureReport += "\n\nИзменения Workspace отменены: исходные файлы восстановлены, новые файлы удалены.";
            return new TaskExecutionResult(task.Id, false, failureReport, totalToolCalls, allTrace.ToArray());
        }
        catch
        {
            recovery.Rollback();
            throw;
        }
    }

    private static string BuildFeedback(TaskValidationReport report, int completedAttempts, int maxCorrectionAttempts)
    {
        var builder = new StringBuilder(1024)
            .AppendLine("Предыдущая попытка не прошла независимую проверку.")
            .AppendLine($"Завершено попыток: {completedAttempts}; доступно исправлений: {maxCorrectionAttempts - completedAttempts + 1}.")
            .AppendLine("Исправь только перечисленные проблемы, затем повторно выполни необходимые проверки.")
            .AppendLine();
        foreach (string issue in report.Issues)
        {
            string bounded = issue.Length <= 12000 ? issue : issue[..12000] + "\n[диагностика обрезана]";
            builder.Append("- ").AppendLine(bounded);
        }
        return builder.ToString();
    }

    private static string AppendValidation(
        string report,
        TaskValidationReport validation,
        int correctionAttempts,
        int totalToolCalls)
    {
        var builder = new StringBuilder(report.Length + 1024)
            .AppendLine(report.TrimEnd())
            .AppendLine()
            .AppendLine("## Независимая проверка")
            .AppendLine($"Результат: {(validation.Success ? "пройдена" : "не пройдена")}.")
            .AppendLine($"Попыток исправления: {correctionAttempts}.")
            .AppendLine($"Всего вызовов инструментов: {totalToolCalls}.");

        foreach (string check in validation.Checks)
            builder.Append("- ").AppendLine(check);
        foreach (string issue in validation.Issues)
            builder.Append("- ПРОБЛЕМА: ").AppendLine(issue);

        return builder.ToString();
    }
}
