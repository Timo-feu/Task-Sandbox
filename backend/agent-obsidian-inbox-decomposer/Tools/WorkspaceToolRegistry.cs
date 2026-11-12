using System.Text;
using TaskSandbox.Obsidian;
using TaskSandbox.Execution;

namespace TaskSandbox.Tools;

public sealed record ToolExecutionResult(bool Success, string Output);

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

    public WorkspaceToolRegistry(
        string workspacePath,
        DependencyPolicy dependencyPolicy,
        ExecutionService executionService,
        IReadOnlyList<string>? allowedWritePaths = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspacePath));
        if (!Directory.Exists(_root))
            throw new DirectoryNotFoundException($"Workspace не найден: {_root}");
        _rootPrefix = _root + Path.DirectorySeparatorChar;
        _dependencyPolicy = dependencyPolicy;
        _executionService = executionService ?? throw new ArgumentNullException(nameof(executionService));
        _allowedWritePaths = allowedWritePaths?.Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizeRelativePath).ToArray() ?? [];
        for (DirectoryInfo? current = new DirectoryInfo(_root); current is not null; current = current.Parent)
            EnsureNoReparsePoint(current.FullName);
    }

    public string DescribeTools() => """
        Доступные инструменты (вызывай строго по одному):
        - list_files: {"path":"относительный/каталог"} — список файлов и папок; path необязателен, по умолчанию корень.
        - read_file: {"path":"относительный/файл"} — чтение UTF-8 файла.
        - write_file: {"path":"относительный/файл","content":"полное содержимое"} — создание или полная перезапись файла.
        - dotnet_build: {"project":"relative/path.csproj","configuration":"Debug"} — dotnet build. project необязателен, но если в корне несколько проектов, укажи путь.
        - dotnet_test: {"project":"relative/path.csproj","configuration":"Debug"} — dotnet test. project необязателен, но если в корне несколько проектов, укажи путь.
        Если задача содержит target_files, изменяй только перечисленные пути или файлы внутри перечисленных каталогов.
        Для сборки/тестов разрешены только dotnet build и dotnet test. Никакие произвольные shell-команды не поддерживаются.
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
            if (tool is "dotnet_build" or "dotnet_test")
            {
                ProcessExecutionResult process = await _executionService
                    .ExecuteAsync(tool, arguments, cancellationToken).ConfigureAwait(false);
                string status = process.TimedOut ? "TIMEOUT" : process.ExitCode?.ToString() ?? "NO_EXIT_CODE";
                string output = $"Статус: {status}; длительность: {process.Duration.TotalSeconds:0.0} сек.; " +
                                $"код завершения: {process.ExitCode?.ToString() ?? "нет"}.\n{process.Output}";
                return new ToolExecutionResult(process.Success, output);
            }

            return tool switch
            {
                "list_files" => ListFiles(GetString(arguments, "path", required: false) ?? string.Empty),
                "read_file" => ReadFile(GetString(arguments, "path")!),
                "write_file" => WriteFile(GetString(arguments, "path")!, GetString(arguments, "content")!),
                _ => new ToolExecutionResult(false, $"Неизвестный инструмент '{tool}'.")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          ArgumentException or InvalidDataException or NotSupportedException or
                                          System.ComponentModel.Win32Exception)
        {
            return new ToolExecutionResult(false, $"{exception.GetType().Name}: {exception.Message}");
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

        string temporaryPath = path + ".agent-tmp-" + Guid.NewGuid().ToString("N");
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
