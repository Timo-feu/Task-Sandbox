using TaskSandbox.Tasks;

namespace TaskSandbox.Prompting;

/// <summary>Isolated context for one planning request. Never shared with executor conversation history.</summary>
public sealed record TaskPlanningContext(
    UserTask Task,
    string ProjectMemoryMarkdown);
