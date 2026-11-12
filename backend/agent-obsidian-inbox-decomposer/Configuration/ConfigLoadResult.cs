namespace TaskSandbox.Configuration;

public sealed record ConfigLoadResult(
    AgentSettings Settings,
    string SettingsPath,
    bool WasCreated,
    bool WasReset,
    string? BackupPath,
    string? ResetReason);
