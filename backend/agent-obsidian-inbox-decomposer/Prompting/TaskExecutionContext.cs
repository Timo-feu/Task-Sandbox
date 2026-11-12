using TaskSandbox.Tasks;

namespace TaskSandbox.Prompting;

/// <summary>Ephemeral context for executing exactly one task.</summary>
public sealed record TaskExecutionContext(
    UserTask Task,
    string ProjectMemoryMarkdown);
