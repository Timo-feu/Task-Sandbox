namespace TaskSandbox.Prompting;

public sealed record PromptSet(
    string DirectoryPath,
    string SystemPrompt,
    string ProjectScanPrompt,
    string TaskExecutionPrompt,
    PromptFeatures Features);
