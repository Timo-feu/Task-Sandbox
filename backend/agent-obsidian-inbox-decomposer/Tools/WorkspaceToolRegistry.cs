using System.Text;
using TaskSandbox.Obsidian;
using TaskSandbox.Execution;

namespace TaskSandbox.Tools;

public sealed record ToolExecutionResult(bool Success, string Output);

public sealed record TaskToolTrace(
    string Tool,
    bool Success,
    string? Path = null,
    int? ExitCode = null,
    bool TimedOut = false,
    string? Detail = null);

/// <summary>Small, allow-listed set of file tools restricted to one Workspace.</summary>
public sealed class WorkspaceToolRegistry
{
    private const int MaxReadBytes = 512 * 1024;
    private const int MaxWriteBytes = 512 * 1024;
    private const int MaxListedFiles = 300;
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly string _root;
    private readonly string _rootPrefix;
    private readonly DependencyPolicy _dependencyPolicy;
    private readonly string[] _allowedWritePaths;
    private readonly ExecutionService _executionService;
    private readonly GitService _gitService;
    private readonly FileRecoverySession? _recoverySession;
    private readonly List<TaskToolTrace> _trace = [];

    public IReadOnlyList<TaskToolTrace> Trace => _trace;

    public WorkspaceToolRegistry(
        string workspacePath,
        DependencyPolicy dependencyPolicy,
        ExecutionService executionService,
        GitService gitService,
        IReadOnlyList<string>? allowedWritePaths = null,
        FileRecoverySession? recoverySession = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspacePath));
        if (!Directory.Exists(_root))
            throw new DirectoryNotFoundException($"Workspace не найден: {_root}");
        _rootPrefix = _root + Path.DirectorySeparatorChar;
        _dependencyPolicy = dependencyPolicy;
        _executionService = executionService ?? throw new ArgumentNullException(nameof(executionService));
        _gitService = gitService ?? throw new ArgumentNullException(nameof(gitService));
        _recoverySession = recoverySession;
        _allowedWritePaths = allowedWritePaths?.Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizeRelativePath).ToArray() ?? [];
        for (DirectoryInfo? current = new DirectoryInfo(_root); current is not null; current = current.Parent)
            EnsureNoReparsePoint(current.FullName);
    }

    public string DescribeTools() => """
        Доступные инструменты (вызывай строго по одному):
        - list_files: {"path":"относительный/каталог"} — список файлов и папок; path необязателен, по умолчанию корень.
        - read_file: {"path":"относительный/файл"} — чтение UTF-8 файла.
        - write_file: {"path":"относительный/файл","content":"полное содержимое"} — создание или полная перезапись файла. Исходная версия автоматически сохраняется для отката задачи.
        - project_build: {"project":"relative/project-directory","configuration":"Debug"} — сборка по автоматически определённому профилю проекта.
        - project_test: {"project":"relative/project-directory","configuration":"Debug"} — тесты по автоматически определённому профилю проекта.
        - git_status: {} — краткий статус Git-репозитория (только чтение).
        - git_diff: {} — diff индексированных и отслеживаемых неиндексированных изменений (только чтение; untracked-файлы видны в git_status).
        Git-инструменты доступны только для чтения. Создание коммитов выполняется отдельно пользователем через CLI с явным списком путей. Push и сетевые Git-операции недоступны.
        Поддерживаемые профили: .NET, Node.js (npm/pnpm/yarn), Python, Go, Rust, Java (Maven/Gradle). Команды выбираются приложением по manifest-файлам; произвольные команды и shell не поддерживаются.
        project необязателен: по умолчанию используется корень Workspace. Если проект находится в подкаталоге, укажи относительный путь к нему.
        Если задача содержит target_files, изменяй только перечисленные пути или файлы внутри перечисленных каталогов.
        Не используй абсолютные пути, '..', ссылки и пути вне Workspace. Не удаляй файлы: такого инструмента нет.
        Для завершения верни {"type":"final","summary":"что сделано","checks":["что проверено"]}.
        Для инструмента верни {"type":"tool_call","tool":"имя","arguments":{...}}.
        """;

    public async Task<ToolExecutionResult> ExecuteAsync(
        string tool,
        System.Text.Json.JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (tool is "git_status" or "git_diff")
            {
                GitCommandResult git = tool == "git_status"
                    ? await _gitService.GetStatusAsync(cancellationToken).ConfigureAwait(false)
                    : await _gitService.GetDiffAsync(cancellationToken).ConfigureAwait(false);
                _trace.Add(new TaskToolTrace(tool, git.Success, Detail: git.Success ? null : git.Output));
                return new ToolExecutionResult(git.Success, git.Output);
            }

            if (tool is "project_build" or "project_test" or "dotnet_build" or "dotnet_test")
            {
                ProcessExecutionResult process = await _executionService
                    .ExecuteAsync(tool, arguments, cancellationToken).ConfigureAwait(false);
                string status = process.TimedOut ? "TIMEOUT" : process.ExitCode?.ToString() ?? "NO_EXIT_CODE";
                string output = $"Профиль: {process.Profile}; статус: {status}; длительность: {process.Duration.TotalSeconds:0.0} сек.; " +
                                $"код завершения: {process.ExitCode?.ToString() ?? "нет"}.\n{process.Output}";
                _trace.Add(new TaskToolTrace(tool, process.Success, ExitCode: process.ExitCode,
                    TimedOut: process.TimedOut, Detail: process.Success ? null : output));
                return new ToolExecutionResult(process.Success, output);
            }

            ToolExecutionResult result = tool switch
            {
                "list_files" => ListFiles(GetString(arguments, "path", required: false) ?? string.Empty),
                "read_file" => ReadFile(GetString(arguments, "path")!),
                "write_file" => WriteFile(GetString(arguments, "path")!, GetString(arguments, "content")!),
                _ => new ToolExecutionResult(false, $"Неизвестный инструмент '{tool}'.")
            };
            string? path = tool == "write_file" && result.Success
                ? NormalizeRelativePath(GetString(arguments, "path")!)
                : null;
            _trace.Add(new TaskToolTrace(tool, result.Success, path,
                Detail: result.Success ? null : result.Output));
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or
                                          ArgumentException or InvalidDataException or NotSupportedException or
                                          System.ComponentModel.Win32Exception)
        {
            string error = $"{exception.GetType().Name}: {exception.Message}";
            _trace.Add(new TaskToolTrace(tool, false, Detail: error));
            return new ToolExecutionResult(false, error);
        }
    }

    private ToolExecutionResult ListFiles(string relativePath)
    {
        string directory = ResolvePath(relativePath, allowRoot: true);
        if (!Directory.Exists(directory))
            return new ToolExecutionResult(false, $"Каталог не найден: {relativePath}");
        EnsurePathHasNoReparsePoints(directory);

        var output = new StringBuilder(1024);
        int count = 0;
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly)
                     .OrderBy(static item => item, StringComparer.OrdinalIgnoreCase))
        {
            if (++count > MaxListedFiles)
            {
                output.AppendLine("... список ограничен 300 элементами");
                break;
            }

            EnsureNoReparsePoint(entry);
            output.Append(Directory.Exists(entry) ? "[DIR] " : "[FILE] ")
                .AppendLine(Path.GetRelativePath(_root, entry));
        }

        return new ToolExecutionResult(true, output.Length == 0 ? "Каталог пуст." : output.ToString());
    }

    private ToolExecutionResult ReadFile(string relativePath)
    {
        string path = ResolvePath(relativePath, allowRoot: false);
        if (!File.Exists(path))
            return new ToolExecutionResult(false, $"Файл не найден: {relativePath}");
        EnsurePathHasNoReparsePoints(path);
        var info = new FileInfo(path);
        if (info.Length > MaxReadBytes)
            return new ToolExecutionResult(false, $"Файл превышает лимит чтения {MaxReadBytes} байт.");

        return new ToolExecutionResult(true, File.ReadAllText(path, Encoding.UTF8));
    }

    private ToolExecutionResult WriteFile(string relativePath, string content)
    {
        string path = ResolvePath(relativePath, allowRoot: false);
        byte[] bytes = Utf8NoBom.GetBytes(content);
        if (bytes.Length > MaxWriteBytes)
            return new ToolExecutionResult(false, $"Содержимое превышает лимит записи {MaxWriteBytes} байт.");
        if (_allowedWritePaths.Length > 0 && !_allowedWritePaths.Any(target =>
                string.Equals(NormalizeRelativePath(relativePath), target, StringComparison.OrdinalIgnoreCase) ||
                NormalizeRelativePath(relativePath).StartsWith(target.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase)))
            return new ToolExecutionResult(false,
                "Запись заблокирована: путь не входит в target_files текущей задачи.");
        if (_dependencyPolicy == DependencyPolicy.ExistingOnly && IsDependencyManifest(relativePath))
            return new ToolExecutionResult(false,
                "Запись заблокирована dependency_policy=existing_only: нельзя изменять манифесты зависимостей.");

        string directory = Path.GetDirectoryName(path)!;
        EnsureDirectoryChain(directory);
        if (File.Exists(path))
            EnsureNoReparsePoint(path);

        _recoverySession?.CaptureBeforeWrite(Path.GetRelativePath(_root, path), path);

        string temporaryPath = path + ".agent-tmp-" + Guid.NewGuid().ToString("N");
        _recoverySession?.CaptureBeforeWrite(Path.GetRelativePath(_root, temporaryPath), temporaryPath);
        try
        {
            File.WriteAllBytes(temporaryPath, bytes);
            EnsureNoReparsePoint(temporaryPath);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }

        return new ToolExecutionResult(true, $"Файл записан: {Path.GetRelativePath(_root, path)} ({bytes.Length} байт).");
    }

    private string ResolvePath(string relativePath, bool allowRoot)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            if (allowRoot) return _root;
            throw new ArgumentException("Необходимо указать относительный путь.");
        }
        if (Path.IsPathRooted(relativePath) || relativePath.Contains(':') ||
            relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Any(static part => part is "." or ".."))
            throw new InvalidDataException("Разрешены только относительные пути без '.' и '..'.");

        string fullPath = Path.GetFullPath(Path.Combine(_root, relativePath));
        if (!fullPath.StartsWith(_rootPrefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) &&
            !string.Equals(fullPath, _root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("Путь выходит за пределы Workspace.");
        return fullPath;
    }

    private void EnsureDirectoryChain(string directory)
    {
        string relative = Path.GetRelativePath(_root, directory);
        string current = _root;
        EnsureNoReparsePoint(current);
        if (relative == ".") return;
        foreach (string part in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            if (Directory.Exists(current))
            {
                EnsureNoReparsePoint(current);
                continue;
            }
            if (File.Exists(current))
                throw new IOException($"На месте каталога находится файл: {current}");
            Directory.CreateDirectory(current);
            EnsureNoReparsePoint(current);
        }
    }

    private void EnsurePathHasNoReparsePoints(string path)
    {
        string relative = Path.GetRelativePath(_root, path);
        string current = _root;
        EnsureNoReparsePoint(current);
        if (relative == ".") return;
        foreach (string part in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            if (File.Exists(current) || Directory.Exists(current)) EnsureNoReparsePoint(current);
        }
    }

    private static void EnsureNoReparsePoint(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"Символьные ссылки и reparse points запрещены: {path}");
    }

    private static bool IsDependencyManifest(string path)
    {
        string name = Path.GetFileName(path);
        string extension = Path.GetExtension(name);
        return name.Equals("packages.config", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("packages.lock.json", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("package.json", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("package-lock.json", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("pnpm-lock.yaml", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("yarn.lock", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Cargo.toml", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Cargo.lock", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("go.mod", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("go.sum", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("pyproject.toml", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("requirements.txt", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Pipfile", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Pipfile.lock", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Directory.Packages.props", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("global.json", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".fsproj", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".vbproj", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".props", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".targets", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRelativePath(string path)
    {
        string normalized = path.Replace('\\', '/').Trim();
        return normalized.StartsWith("./", StringComparison.Ordinal) ? normalized[2..] : normalized;
    }

    private static string? GetString(System.Text.Json.JsonElement arguments, string name, bool required = true)
    {
        if (arguments.ValueKind != System.Text.Json.JsonValueKind.Object || !arguments.TryGetProperty(name, out var value))
        {
            if (required) throw new InvalidDataException($"Не указан аргумент '{name}'.");
            return null;
        }
        if (value.ValueKind != System.Text.Json.JsonValueKind.String)
            throw new InvalidDataException($"Аргумент '{name}' должен быть строкой.");
        return value.GetString();
    }
}
