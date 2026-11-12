namespace TaskSandbox.Prompting;

public sealed record PromptSet(
    string DirectoryPath,
    string SystemPrompt,
    string TaskPlanningPrompt,
    string TaskExecutionPrompt,
    PromptFeatures Features);
