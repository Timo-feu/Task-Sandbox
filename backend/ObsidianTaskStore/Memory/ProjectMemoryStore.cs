using System.Security.Cryptography;
using System.Text;

namespace TaskSandbox.Memory;

/// <summary>Stores one Markdown memory file per workspace outside the workspace directory.</summary>
public sealed class ProjectMemoryStore
{
    private const string MemoryFileName = "ProjectMemory.md";
    private const int MaxMemoryBytes = 256 * 1024;
    private readonly string _rootDirectory;

    public ProjectMemoryStore(string? rootDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(rootDirectory))
        {
            _rootDirectory = Path.GetFullPath(rootDirectory);
            return;
        }

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
            localAppData = AppContext.BaseDirectory;

        _rootDirectory = Path.Combine(localAppData, "LocalCodingAgent", "WorkspaceMemory");
    }

    public string GetMemoryPath(string workspacePath)
    {
        string normalizedWorkspace = NormalizeWorkspacePath(workspacePath);
        string hashInput = OperatingSystem.IsWindows()
            ? normalizedWorkspace.ToUpperInvariant()
            : normalizedWorkspace;

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(hashInput));
        string workspaceKey = Convert.ToHexString(hash);
        return Path.Combine(_rootDirectory, workspaceKey[..2], workspaceKey, MemoryFileName);
    }

    public async Task<string> LoadAsync(
        string workspacePath,
        CancellationToken cancellationToken = default)
    {
        string memoryPath = GetMemoryPath(workspacePath);
        if (File.Exists(memoryPath))
        {
            if (new FileInfo(memoryPath).Length > MaxMemoryBytes)
                throw new InvalidDataException(
                    $"Файл {MemoryFileName} превышает лимит {MaxMemoryBytes} байт.");

            return await File.ReadAllTextAsync(memoryPath, cancellationToken).ConfigureAwait(false);
        }

        string? directory = Path.GetDirectoryName(memoryPath);
        if (directory is not null)
            Directory.CreateDirectory(directory);

        string initialMemory = CreateEmptyMemory(workspacePath);
        await File.WriteAllTextAsync(memoryPath, initialMemory, Encoding.UTF8, cancellationToken)
            .ConfigureAwait(false);

        return initialMemory;
    }

    public async Task SaveAsync(
        string workspacePath,
        string markdown,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        if (Encoding.UTF8.GetByteCount(markdown) > MaxMemoryBytes)
            throw new ArgumentException(
                $"Project memory exceeds the {MaxMemoryBytes}-byte limit.", nameof(markdown));

        string memoryPath = GetMemoryPath(workspacePath);
        string? directory = Path.GetDirectoryName(memoryPath);
        if (directory is not null)
            Directory.CreateDirectory(directory);

        await File.WriteAllTextAsync(memoryPath, markdown, Encoding.UTF8, cancellationToken)
            .ConfigureAwait(false);
    }

    private static string NormalizeWorkspacePath(string workspacePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
            throw new ArgumentException("Workspace path cannot be empty.", nameof(workspacePath));

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspacePath));
    }

    private static string CreateEmptyMemory(string workspacePath) =>
        $"# Project Memory{Environment.NewLine}" +
        $"> Workspace: `{NormalizeWorkspacePath(workspacePath)}`{Environment.NewLine}" +
        $"> This file is stored outside the workspace. Facts must be verified against current project files when needed.{Environment.NewLine}{Environment.NewLine}" +
        $"## Desired Outcome{Environment.NewLine}" +
        $"_Not recorded yet._{Environment.NewLine}{Environment.NewLine}" +
        $"## Current State{Environment.NewLine}" +
        $"_Not analyzed yet._{Environment.NewLine}{Environment.NewLine}" +
        $"## Completed{Environment.NewLine}" +
        $"_Nothing verified yet._{Environment.NewLine}{Environment.NewLine}" +
        $"## Next Steps{Environment.NewLine}" +
        $"_Not determined yet._{Environment.NewLine}{Environment.NewLine}" +
        $"## Architecture and Technologies{Environment.NewLine}" +
        $"_Not analyzed yet._{Environment.NewLine}{Environment.NewLine}" +
        $"## Important Files{Environment.NewLine}" +
        $"_Not analyzed yet._{Environment.NewLine}{Environment.NewLine}" +
        $"## Constraints and Decisions{Environment.NewLine}" +
        $"_Not recorded yet._{Environment.NewLine}{Environment.NewLine}" +
        $"## Open Questions{Environment.NewLine}" +
        $"_None recorded._{Environment.NewLine}";
}
