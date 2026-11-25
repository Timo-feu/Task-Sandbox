using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TaskSandbox.Execution;

/// <summary>Stores per-task file snapshots outside Workspace and rolls back interrupted tasks.</summary>
public sealed class WorkspaceRecoveryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly string _workspaceRoot;
    private readonly string _recoveryRoot;
    private readonly StringComparison _pathComparison;

    public WorkspaceRecoveryStore(string workspacePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        _workspaceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspacePath));
        if (!Directory.Exists(_workspaceRoot))
            throw new DirectoryNotFoundException($"Workspace не найден: {_workspaceRoot}");
        EnsureWorkspaceHasNoReparsePoints(_workspaceRoot);

        _pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
            localData = Path.GetTempPath();

        string workspaceKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_workspaceRoot)));
        _recoveryRoot = Path.Combine(localData, "LocalCodingAgent", "Recovery", workspaceKey);
        Directory.CreateDirectory(_recoveryRoot);
    }

    private static void EnsureWorkspaceHasNoReparsePoints(string workspaceRoot)
    {
        for (DirectoryInfo? current = new DirectoryInfo(workspaceRoot); current is not null; current = current.Parent)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Workspace содержит ссылку/reparse point: {current.FullName}");
        }
    }

    public FileRecoverySession BeginTask(string taskId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        string sessionDirectory = Path.Combine(_recoveryRoot, DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(sessionDirectory, "files"));
        var journal = new RecoveryJournal(_workspaceRoot, taskId, "active", []);
        var session = new FileRecoverySession(_workspaceRoot, sessionDirectory, journal, _pathComparison);
        session.SaveJournal();
        return session;
    }

    /// <summary>Restores sessions left active by a process crash. Committed snapshots are retained.</summary>
    public int RecoverInterruptedSessions()
    {
        int recovered = 0;
        foreach (string directory in Directory.EnumerateDirectories(_recoveryRoot))
        {
            string journalPath = Path.Combine(directory, "journal.json");
            if (!File.Exists(journalPath))
                continue;

            RecoveryJournal? journal = JsonSerializer.Deserialize<RecoveryJournal>(File.ReadAllText(journalPath), JsonOptions);
            if (journal is null || !string.Equals(journal.State, "active", StringComparison.Ordinal))
                continue;
            if (!string.Equals(Path.GetFullPath(journal.WorkspaceRoot), _workspaceRoot, _pathComparison))
                continue;

            var session = new FileRecoverySession(_workspaceRoot, directory, journal, _pathComparison);
            session.Rollback();
            recovered++;
        }
        return recovered;
    }
}

public sealed record RecoveryFileEntry(string RelativePath, bool Existed, string? BackupFile);
public sealed record RecoveryJournal(
    string WorkspaceRoot,
    string TaskId,
    string State,
    List<RecoveryFileEntry> Files);

/// <summary>Captures each file's original state once, before its first write in a task.</summary>
public sealed class FileRecoverySession
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _workspaceRoot;
    private readonly string _workspacePrefix;
    private readonly string _sessionDirectory;
    private readonly StringComparison _pathComparison;
    private readonly HashSet<string> _captured;
    private RecoveryJournal _journal;

    internal FileRecoverySession(
        string workspaceRoot,
        string sessionDirectory,
        RecoveryJournal journal,
        StringComparison pathComparison)
    {
        _workspaceRoot = workspaceRoot;
        _workspacePrefix = workspaceRoot + Path.DirectorySeparatorChar;
        _sessionDirectory = sessionDirectory;
        _journal = journal;
        _pathComparison = pathComparison;
        _captured = new HashSet<string>(journal.Files.Select(static item => item.RelativePath),
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    }

    public void CaptureBeforeWrite(string relativePath, string fullPath)
    {
        if (_journal.State != "active")
            throw new InvalidOperationException("Recovery-сессия уже завершена.");

        string resolved = Path.GetFullPath(fullPath);
        if (!resolved.StartsWith(_workspacePrefix, _pathComparison))
            throw new InvalidOperationException("Нельзя сохранить состояние файла вне Workspace.");
        if (_captured.Contains(relativePath))
            return;

        string? backupFile = null;
        bool existed = File.Exists(resolved);
        if (existed)
        {
            EnsureNoReparsePoints(resolved);
            backupFile = Path.Combine("files", _journal.Files.Count.ToString("D6") + ".bin");
            File.Copy(resolved, Path.Combine(_sessionDirectory, backupFile), overwrite: false);
        }

        var files = new List<RecoveryFileEntry>(_journal.Files)
        {
            new(relativePath, existed, backupFile)
        };
        RecoveryJournal updated = _journal with { Files = files };
        SaveJournal(updated);
        _journal = updated;
        _captured.Add(relativePath);
    }

    public void Commit()
    {
        if (_journal.State != "active")
            return;
        RecoveryJournal updated = _journal with { State = "committed" };
        SaveJournal(updated);
        _journal = updated;
    }

    public void Rollback()
    {
        if (_journal.State != "active")
            return;

        for (int i = _journal.Files.Count - 1; i >= 0; i--)
        {
            RecoveryFileEntry entry = _journal.Files[i];
            string target = ResolveRecordedPath(entry.RelativePath);
            string directory = Path.GetDirectoryName(target)!;

            if (entry.Existed)
            {
                string backup = Path.GetFullPath(Path.Combine(_sessionDirectory, entry.BackupFile!));
                string backupPrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_sessionDirectory)) + Path.DirectorySeparatorChar;
                if (!backup.StartsWith(backupPrefix, _pathComparison))
                    throw new InvalidDataException("Путь резервной копии в recovery-журнале выходит за пределы сессии.");
                if (!File.Exists(backup))
                    throw new IOException($"Не найдена резервная копия файла '{entry.RelativePath}'.");
                EnsureNoReparsePoints(directory);
                Directory.CreateDirectory(directory);
                EnsureNoReparsePoints(directory);
                string temporary = target + ".agent-restore-" + Guid.NewGuid().ToString("N");
                File.Copy(backup, temporary, overwrite: false);
                File.Move(temporary, target, overwrite: true);
            }
            else if (File.Exists(target))
            {
                EnsureNoReparsePoints(target);
                File.Delete(target);
            }
        }

        RecoveryJournal updated = _journal with { State = "rolled_back" };
        SaveJournal(updated);
        _journal = updated;
    }

    internal void SaveJournal() => SaveJournal(_journal);

    private void SaveJournal(RecoveryJournal journal)
    {
        string journalPath = Path.Combine(_sessionDirectory, "journal.json");
        string temporaryPath = journalPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(journal, JsonOptions), new UTF8Encoding(false));
        File.Move(temporaryPath, journalPath, overwrite: true);
    }

    private string ResolveRecordedPath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) ||
            relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
                .Any(static part => part is "." or ".."))
            throw new InvalidDataException($"Некорректный путь в recovery-журнале: '{relativePath}'.");

        string fullPath = Path.GetFullPath(Path.Combine(_workspaceRoot, relativePath));
        if (!fullPath.StartsWith(_workspacePrefix, _pathComparison))
            throw new InvalidDataException($"Путь в recovery-журнале выходит за Workspace: '{relativePath}'.");
        return fullPath;
    }

    private void EnsureNoReparsePoints(string path)
    {
        string? current = path;
        while (!string.IsNullOrEmpty(current) &&
               (current.StartsWith(_workspacePrefix, _pathComparison) ||
                string.Equals(current, _workspaceRoot, _pathComparison)))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Recovery заблокирован: путь содержит ссылку '{current}'.");
            if (string.Equals(current, _workspaceRoot, _pathComparison))
                break;
            current = Path.GetDirectoryName(current);
        }
    }
}
