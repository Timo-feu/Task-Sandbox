namespace TaskSandbox.Prompting;

/// <summary>Ephemeral context for one user task. This is level-two task memory.</summary>
public sealed record TaskPromptContext(
    int TaskId,
    string WorkspacePath,
    string Description,
    string TargetFiles,
    string Requirements,
    string CurrentProgress,
    string CompletedWork);

/// <summary>Input collected by the deterministic workspace scanner before an AI analysis pass.</summary>
public sealed record ProjectScanInput(
    string WorkspacePath,
    int FileCount,
    int DirectoryCount,
    string InventoryMarkdown,
    string KeyFileContents);
