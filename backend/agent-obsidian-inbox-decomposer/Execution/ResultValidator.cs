using TaskSandbox.Obsidian;
using TaskSandbox.Tools;

namespace TaskSandbox.Execution;

public sealed record TaskValidationReport(
    bool Success,
    IReadOnlyList<string> Checks,
    IReadOnlyList<string> Issues);

/// <summary>Performs deterministic checks against Workspace state and recorded tool results.</summary>
public sealed class ResultValidator
{
    private readonly string _workspaceRoot;
    private readonly string _workspacePrefix;
    private readonly StringComparison _pathComparison;

    public ResultValidator(string workspacePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        _workspaceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspacePath));
        if (!Directory.Exists(_workspaceRoot))
            throw new DirectoryNotFoundException($"Workspace не найден: {_workspaceRoot}");
        _workspacePrefix = _workspaceRoot + Path.DirectorySeparatorChar;
        _pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    }

    public TaskValidationReport Validate(ObsidianTaskDocument task, TaskExecutionResult result)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(result);

        var checks = new List<string>(task.TargetFiles.Count + result.ToolTrace.Count + 2);
        var issues = new List<string>();

        if (!result.Success)
            issues.Add("Исполнитель сообщил success=false. Нужно устранить причину и повторить задачу.");
        else
            checks.Add("Исполнитель сообщил об успешном завершении.");

        foreach (string relativePath in task.TargetFiles)
        {
            if (!TryResolveWorkspacePath(relativePath, out string? fullPath, out string? error))
            {
                issues.Add($"Целевой путь '{relativePath}' некорректен: {error}");
                continue;
            }

            if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
            {
                issues.Add($"Целевой файл или каталог не найден после выполнения: '{relativePath}'.");
                continue;
            }

            if (ContainsReparsePoint(fullPath))
            {
                issues.Add($"Целевой путь содержит ссылку/reparse point: '{relativePath}'.");
                continue;
            }

            checks.Add($"Целевой путь существует: {relativePath}.");
        }

        int processChecks = 0;
        bool successfulBuild = false;
        bool successfulTest = false;
        foreach (TaskToolTrace trace in result.ToolTrace)
        {
            if (trace.Tool is not ("project_build" or "project_test" or "dotnet_build" or "dotnet_test"))
                continue;

            if (trace.ExitCode is null && !trace.TimedOut)
            {
                checks.Add($"{trace.Tool}: процесс не был запущен. {trace.Detail ?? "Причина не указана."}");
                continue;
            }

            processChecks++;
            if (!trace.Success || trace.TimedOut || trace.ExitCode != 0)
            {
                string details = string.IsNullOrWhiteSpace(trace.Detail)
                    ? $"exit code: {trace.ExitCode?.ToString() ?? "нет"}, timeout: {trace.TimedOut}"
                    : trace.Detail;
                issues.Add($"Проверка {trace.Tool} не пройдена. Диагностика:\n{details}");
            }
            else
            {
                checks.Add($"{trace.Tool} завершился с exit code 0.");
                successfulBuild |= trace.Tool is "project_build" or "dotnet_build";
                successfulTest |= trace.Tool is "project_test" or "dotnet_test";
            }
        }

        BuildTestProfile? profile = null;
        try
        {
            profile = BuildTestProfile.Detect(_workspaceRoot);
        }
        catch (System.Text.Json.JsonException exception)
        {
            issues.Add($"Не удалось прочитать manifest проекта для определения профиля: {exception.Message}");
        }
        bool hasProjectCode = profile is not null && task.TargetFiles.Any(path =>
            profile.SourceExtensions.Contains(Path.GetExtension(path)));
        if (hasProjectCode && profile!.Build is not null && !successfulBuild)
            issues.Add($"Изменён исходный код проекта {profile.Name}, но нет успешной project_build. Запусти сборку и исправь ошибки.");
        else if (processChecks == 0)
            checks.Add("Сборка и тесты не запускались; автоматическая проверка не подтверждена.");

        if (hasProjectCode && profile?.Test is not null && !successfulTest)
            issues.Add($"Изменён исходный код проекта {profile.Name}, но нет успешного project_test. Запусти тесты и исправь ошибки.");
        else if (profile?.Test is not null)
            checks.Add("Профиль проекта содержит тестовую команду.");

        if (task.AcceptanceCriteria.Count > 0)
            checks.Add("Критерии приёмки переданы исполнителю; их смысловая проверка не может быть подтверждена только файловыми проверками.");

        return new TaskValidationReport(issues.Count == 0, checks, issues);
    }

    private bool TryResolveWorkspacePath(string relativePath, out string? fullPath, out string? error)
    {
        fullPath = null;
        error = null;
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) ||
            relativePath.Contains(':') || relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
                .Any(static part => part is "." or ".."))
        {
            error = "ожидался безопасный относительный путь без '.' и '..'";
            return false;
        }

        string resolved = Path.GetFullPath(Path.Combine(_workspaceRoot, relativePath));
        if (!resolved.StartsWith(_workspacePrefix, _pathComparison) &&
            !string.Equals(resolved, _workspaceRoot, _pathComparison))
        {
            error = "путь выходит за пределы Workspace";
            return false;
        }

        fullPath = resolved;
        return true;
    }

    private bool ContainsReparsePoint(string path)
    {
        string current = path;
        while (!string.IsNullOrEmpty(current) &&
               (current.StartsWith(_workspacePrefix, _pathComparison) ||
                string.Equals(current, _workspaceRoot, _pathComparison)))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                return true;
            if (string.Equals(current, _workspaceRoot, _pathComparison))
                break;
            current = Path.GetDirectoryName(current)!;
        }
        return false;
    }
}
