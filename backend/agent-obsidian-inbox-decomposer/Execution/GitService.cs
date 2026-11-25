using System.Diagnostics;
using System.Text;

namespace TaskSandbox.Execution;

public sealed record GitCommandResult(bool Success, int? ExitCode, string Output);

/// <summary>Restricted Git operations for one Workspace. No shell and no network commands.</summary>
public sealed class GitService
{
    private const int MaxOutputCharacters = 128 * 1024;
    private readonly string _workspaceRoot;
    private readonly TimeSpan _timeout;

    public GitService(string workspacePath, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        _workspaceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspacePath));
        if (!Directory.Exists(_workspaceRoot))
            throw new DirectoryNotFoundException($"Workspace не найден: {_workspaceRoot}");
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
        if (_timeout < TimeSpan.FromSeconds(1) || _timeout > TimeSpan.FromMinutes(2))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        EnsureNoReparsePoints(_workspaceRoot);
    }

    public async Task<GitCommandResult> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        GitCommandResult repository = await EnsureRepositoryAsync(cancellationToken).ConfigureAwait(false);
        if (!repository.Success) return repository;
        return await RunAsync(["status", "--short", "--branch", "--untracked-files=all"], cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<GitCommandResult> GetDiffAsync(CancellationToken cancellationToken = default)
    {
        GitCommandResult repository = await EnsureRepositoryAsync(cancellationToken).ConfigureAwait(false);
        if (!repository.Success) return repository;

        GitCommandResult unstaged = await RunAsync(["diff", "--no-ext-diff", "--no-color", "--"], cancellationToken)
            .ConfigureAwait(false);
        if (!unstaged.Success) return unstaged;
        GitCommandResult staged = await RunAsync(["diff", "--cached", "--no-ext-diff", "--no-color", "--"], cancellationToken)
            .ConfigureAwait(false);
        if (!staged.Success) return staged;

        string output = "## Незастейдженные изменения\n" +
                        (string.IsNullOrWhiteSpace(unstaged.Output) ? "Нет изменений.\n" : unstaged.Output) +
                        "\n## Уже добавленные в индекс изменения\n" +
                        (string.IsNullOrWhiteSpace(staged.Output) ? "Нет изменений.\n" : staged.Output);
        return new GitCommandResult(true, 0, Truncate(output));
    }

    /// <summary>Stages only explicitly selected Workspace-relative paths and creates a local commit.</summary>
    public async Task<GitCommandResult> CommitAsync(
        string message,
        IReadOnlyList<string> relativePaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (message.Length > 200)
            return new GitCommandResult(false, null, "Сообщение коммита не должно превышать 200 символов.");
        if (relativePaths is null || relativePaths.Count == 0)
            return new GitCommandResult(false, null, "Для коммита необходимо явно указать хотя бы один относительный путь.");

        GitCommandResult repository = await EnsureRepositoryAsync(cancellationToken).ConfigureAwait(false);
        if (!repository.Success) return repository;

        var paths = new List<string>(relativePaths.Count);
        foreach (string path in relativePaths)
        {
            string? validated = ValidateRelativePath(path);
            if (validated is null)
                return new GitCommandResult(false, null, $"Недопустимый путь для коммита: '{path}'.");
            string fullPath = Path.GetFullPath(Path.Combine(_workspaceRoot, validated));
            if (!IsInsideWorkspace(fullPath))
                return new GitCommandResult(false, null, $"Путь выходит за пределы Workspace: '{path}'.");
            EnsureExistingPathHasNoReparsePoints(fullPath);
            if (!File.Exists(fullPath))
                return new GitCommandResult(false, null, $"Путь не является существующим файлом: '{path}'. Указывай конкретные файлы, не каталоги.");
            paths.Add(validated);
        }

        GitCommandResult stagedCheck = await RunAsync(["diff", "--cached", "--quiet"], cancellationToken)
            .ConfigureAwait(false);
        if (stagedCheck.ExitCode == 1)
            return new GitCommandResult(false, 1,
                "Коммит отменён: в Git уже есть предварительно добавленные изменения. Сначала проверь git status и очисти индекс вручную.");
        if (!stagedCheck.Success)
            return stagedCheck;

        var addArguments = new List<string> { "add", "-A", "--" };
        addArguments.AddRange(paths.Distinct(StringComparer.OrdinalIgnoreCase));
        GitCommandResult add = await RunAsync(addArguments, cancellationToken).ConfigureAwait(false);
        if (!add.Success) return add;

        GitCommandResult commit = await RunAsync(["commit", "-m", message], cancellationToken).ConfigureAwait(false);
        if (!commit.Success)
            return new GitCommandResult(false, commit.ExitCode,
                "Индекс Git мог быть обновлён, но коммит не создан. Проверь git status.\n" + commit.Output);
        return commit;
    }

    private async Task<GitCommandResult> EnsureRepositoryAsync(CancellationToken cancellationToken)
    {
        GitCommandResult result = await RunAsync(["rev-parse", "--show-toplevel"], cancellationToken)
            .ConfigureAwait(false);
        if (!result.Success)
            return new GitCommandResult(false, result.ExitCode,
                "В Workspace не обнаружен Git-репозиторий или Git недоступен.\n" + result.Output);

        string root = result.Output.Trim();
        if (!string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), _workspaceRoot,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return new GitCommandResult(false, null,
                "Git-репозиторий должен совпадать с корнем Workspace. Репозитории родительских каталогов не используются.");
        return new GitCommandResult(true, 0, string.Empty);
    }

    private async Task<GitCommandResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = _workspaceRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                return new GitCommandResult(false, null, "Не удалось запустить Git.");
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            return new GitCommandResult(false, null, $"Git не найден или недоступен: {exception.Message}");
        }

        Task<string> stdoutTask = ReadBoundedAsync(process.StandardOutput);
        Task<string> stderrTask = ReadBoundedAsync(process.StandardError);
        using var timeoutSource = new CancellationTokenSource(_timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            await process.WaitForExitAsync(linkedSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(cancellationToken);
            return new GitCommandResult(false, null, $"Команда Git превысила лимит {_timeout.TotalSeconds:0} сек.");
        }

        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);
        string output = string.Join(Environment.NewLine, new[] { stdout.TrimEnd(), stderr.TrimEnd() }
            .Where(static value => value.Length > 0));
        int exitCode = process.ExitCode;
        return new GitCommandResult(exitCode == 0, exitCode,
            string.IsNullOrWhiteSpace(output) ? (exitCode == 0 ? string.Empty : "Git завершился с ошибкой без вывода.") : Truncate(output));
    }

    private string? ValidateRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains(':') ||
            path.IndexOfAny(['*', '?', '[', ']']) >= 0) return null;
        string normalized = path.Replace('\\', '/').Trim();
        if (normalized.Length == 0 || normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(static part => part is "." or "..")) return null;
        return normalized;
    }

    private bool IsInsideWorkspace(string path)
    {
        string prefix = _workspaceRoot + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
               string.Equals(path, _workspaceRoot,
                   OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private void EnsureExistingPathHasNoReparsePoints(string path)
    {
        string relative = Path.GetRelativePath(_workspaceRoot, path);
        string current = _workspaceRoot;
        EnsureNoReparsePoints(current);
        if (relative == ".") return;
        foreach (string part in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            if (File.Exists(current) || Directory.Exists(current)) EnsureNoReparsePoints(current);
        }
    }

    private static void EnsureNoReparsePoints(string path)
    {
        string? current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Git-операции запрещены через символические ссылки: {current}");
            current = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(current));
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        var output = new StringBuilder(8192);
        char[] buffer = new char[4096];
        bool truncated = false;
        while (true)
        {
            int read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
            if (read == 0) break;
            int remaining = MaxOutputCharacters - output.Length;
            if (remaining > 0)
                output.Append(buffer, 0, Math.Min(read, remaining));
            if (read > remaining) truncated = true;
        }
        if (truncated) output.Append("\n[Git] Вывод обрезан.");
        return output.ToString();
    }

    private static string Truncate(string value) => value.Length <= MaxOutputCharacters
        ? value
        : value[..MaxOutputCharacters] + "\n[Git] Вывод обрезан.";
}
