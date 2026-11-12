using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace TaskSandbox.Execution;

public sealed record ProcessExecutionResult(
    bool Success,
    int? ExitCode,
    bool TimedOut,
    bool OutputTruncated,
    string Output,
    TimeSpan Duration);

/// <summary>Runs a deliberately small allow-list of .NET CLI operations inside one Workspace.</summary>
public sealed class ExecutionService
{
    private readonly string _workspaceRoot;
    private readonly string _workspacePrefix;
    private readonly TimeSpan _timeout;
    private readonly int _maxOutputCharacters;

    public ExecutionService(string workspacePath, TimeSpan timeout, int maxOutputCharacters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        if (timeout < TimeSpan.FromSeconds(10) || timeout > TimeSpan.FromMinutes(30))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        if (maxOutputCharacters is < 4096 or > 262144)
            throw new ArgumentOutOfRangeException(nameof(maxOutputCharacters));

        _workspaceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspacePath));
        if (!Directory.Exists(_workspaceRoot))
            throw new DirectoryNotFoundException($"Workspace не найден: {_workspaceRoot}");
        _workspacePrefix = _workspaceRoot + Path.DirectorySeparatorChar;
        _timeout = timeout;
        _maxOutputCharacters = maxOutputCharacters;
        EnsureNoReparsePoints(_workspaceRoot);
    }

    public async Task<ProcessExecutionResult> ExecuteAsync(
        string operation,
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        if (operation is not ("dotnet_build" or "dotnet_test"))
            return new ProcessExecutionResult(false, null, false, false,
                $"Операция '{operation}' не разрешена.", TimeSpan.Zero);

        string? target = GetOptionalString(arguments, "project");
        string configuration = GetOptionalString(arguments, "configuration") ?? "Debug";
        if (configuration is not ("Debug" or "Release"))
            return new ProcessExecutionResult(false, null, false, false,
                "configuration должен быть Debug или Release.", TimeSpan.Zero);

        string workingDirectory = _workspaceRoot;
        string? targetPath = null;
        if (!string.IsNullOrWhiteSpace(target))
        {
            targetPath = ResolveTarget(target);
            workingDirectory = Directory.Exists(targetPath) ? targetPath : Path.GetDirectoryName(targetPath)!;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(operation == "dotnet_build" ? "build" : "test");
        if (targetPath is not null)
            startInfo.ArgumentList.Add(targetPath);
        startInfo.ArgumentList.Add("--configuration");
        startInfo.ArgumentList.Add(configuration);
        startInfo.ArgumentList.Add("--nologo");
        startInfo.ArgumentList.Add("--verbosity");
        startInfo.ArgumentList.Add("minimal");

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var output = new BoundedProcessOutput(_maxOutputCharacters);
        var stopwatch = Stopwatch.StartNew();
        if (!process.Start())
            return new ProcessExecutionResult(false, null, false, false,
                "Не удалось запустить dotnet.", stopwatch.Elapsed);

        Task stdoutTask = DrainAsync(process.StandardOutput, output, "stdout");
        Task stderrTask = DrainAsync(process.StandardError, output, "stderr");
        using var timeoutSource = new CancellationTokenSource(_timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutSource.Token);

        bool timedOut = false;
        try
        {
            await process.WaitForExitAsync(linkedSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
            bool callerCancelled = cancellationToken.IsCancellationRequested;
            TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            if (callerCancelled)
                throw new OperationCanceledException(cancellationToken);
        }

        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        stopwatch.Stop();
        int? exitCode = process.HasExited ? process.ExitCode : null;
        string text = output.GetText();
        if (timedOut)
            text += $"\n[ExecutionService] Превышено ограничение времени {_timeout.TotalSeconds:0} сек.; процесс остановлен.";
        if (output.Truncated)
            text += "\n[ExecutionService] Вывод обрезан по установленному лимиту.";

        return new ProcessExecutionResult(!timedOut && exitCode == 0, exitCode,
            timedOut, output.Truncated, text, stopwatch.Elapsed);
    }

    private async Task DrainAsync(StreamReader reader, BoundedProcessOutput output, string streamName)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            output.Append(streamName, line);
    }

    private string ResolveTarget(string relativePath)
    {
        if (Path.IsPathRooted(relativePath) || relativePath.Contains(':') ||
            relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
                .Any(static part => part is "." or ".."))
            throw new InvalidDataException("project должен быть относительным путём внутри Workspace без '.' и '..'.");

        string fullPath = Path.GetFullPath(Path.Combine(_workspaceRoot, relativePath));
        if (!fullPath.StartsWith(_workspacePrefix,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) &&
            !string.Equals(fullPath, _workspaceRoot,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("Путь проекта выходит за пределы Workspace.");
        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
            throw new FileNotFoundException("Проект или решение не найдено.", relativePath);
        EnsureNoReparsePoints(fullPath);

        if (Directory.Exists(fullPath))
        {
            string[] candidates = Directory.EnumerateFiles(fullPath, "*.sln", SearchOption.TopDirectoryOnly)
                .Concat(Directory.EnumerateFiles(fullPath, "*.slnx", SearchOption.TopDirectoryOnly))
                .Concat(Directory.EnumerateFiles(fullPath, "*.csproj", SearchOption.TopDirectoryOnly))
                .ToArray();
            if (candidates.Length != 1)
                throw new InvalidDataException(
                    $"В каталоге должно находиться ровно одно решение или csproj верхнего уровня; найдено: {candidates.Length}. Укажи project явно.");
            fullPath = candidates[0];
            EnsureNoReparsePoints(fullPath);
        }
        else if (!Path.GetExtension(fullPath).Equals(".sln", StringComparison.OrdinalIgnoreCase) &&
                 !Path.GetExtension(fullPath).Equals(".slnx", StringComparison.OrdinalIgnoreCase) &&
                 !Path.GetExtension(fullPath).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Разрешены только файлы .sln, .slnx и .csproj.");
        }
        return fullPath;
    }

    private static string? GetOptionalString(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(name, out JsonElement value))
            return null;
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"Аргумент '{name}' должен быть строкой.");
        return value.GetString();
    }

    private static void EnsureNoReparsePoints(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string? current = File.Exists(fullPath) ? Path.GetDirectoryName(fullPath) : fullPath;
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Символьные ссылки и reparse points запрещены: {current}");
            current = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(current));
        }
        if (File.Exists(fullPath) && (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"Символьные ссылки и reparse points запрещены: {fullPath}");
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private sealed class BoundedProcessOutput(int limit)
    {
        private readonly StringBuilder _builder = new(Math.Min(limit, 8192));
        public bool Truncated { get; private set; }

        public void Append(string stream, string line)
        {
            if (_builder.Length >= limit)
            {
                Truncated = true;
                return;
            }
            string entry = $"[{stream}] {line}{Environment.NewLine}";
            int remaining = limit - _builder.Length;
            if (entry.Length > remaining)
            {
                _builder.Append(entry.AsSpan(0, remaining));
                Truncated = true;
            }
            else
            {
                _builder.Append(entry);
            }
        }

        public string GetText() => _builder.ToString();
    }
}
