using System.Text;

namespace TaskSandbox.Prompting;

/// <summary>Loads editable Markdown prompt templates from a directory outside the workspace.</summary>
public static class PromptLoader
{
    public const string PromptDirectoryEnvironmentVariable = "LOCAL_CODING_AGENT_PROMPTS";

    private const string FlagsStart = "[flags]";
    private const string FlagsEnd = "[/flags]";
    private const int MaxPromptCharacters = 256 * 1024;

    private static readonly string[] PromptFiles =
    [
        "SystemPrompt.md",
        "ProjectScanPrompt.md",
        "TaskExecutionPrompt.md"
    ];

    public static async Task<PromptSet> LoadAsync(
        string? promptDirectory = null,
        CancellationToken cancellationToken = default)
    {
        string resolvedDirectory = ResolvePromptDirectory(promptDirectory);
        await EnsurePromptFilesAsync(resolvedDirectory, cancellationToken).ConfigureAwait(false);

        string systemPromptWithFlags = await ReadPromptAsync(
            Path.Combine(resolvedDirectory, PromptFiles[0]), cancellationToken).ConfigureAwait(false);
        string projectScanPrompt = await ReadPromptAsync(
            Path.Combine(resolvedDirectory, PromptFiles[1]), cancellationToken).ConfigureAwait(false);
        string taskExecutionPrompt = await ReadPromptAsync(
            Path.Combine(resolvedDirectory, PromptFiles[2]), cancellationToken).ConfigureAwait(false);

        (string systemPrompt, PromptFeatures features) = ExtractFlags(systemPromptWithFlags);

        if ((features & PromptFeatures.ProtocolOutput) != 0)
        {
            throw new InvalidDataException(
                "Флаг [protocol-output] пока отключён: Agent Protocol ещё не подключён. " +
                "Удалите [protocol-output] из блока [flags] до следующего этапа.");
        }

        return new PromptSet(
            resolvedDirectory,
            systemPrompt,
            projectScanPrompt,
            taskExecutionPrompt,
            features);
    }

    private static string ResolvePromptDirectory(string? promptDirectory)
    {
        string? configuredDirectory = promptDirectory;
        if (string.IsNullOrWhiteSpace(configuredDirectory))
            configuredDirectory = Environment.GetEnvironmentVariable(PromptDirectoryEnvironmentVariable);

        if (!string.IsNullOrWhiteSpace(configuredDirectory))
            return Path.GetFullPath(configuredDirectory);

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
            localAppData = AppContext.BaseDirectory;

        return Path.Combine(localAppData, "LocalCodingAgent", "Prompts");
    }

    private static async Task EnsurePromptFilesAsync(
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationDirectory);
        string sourceDirectory = Path.Combine(AppContext.BaseDirectory, "PromptTemplates");

        foreach (string fileName in PromptFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string destinationPath = Path.Combine(destinationDirectory, fileName);
            if (File.Exists(destinationPath))
                continue;

            string sourcePath = Path.Combine(sourceDirectory, fileName);
            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException(
                    $"Не найден шаблон '{fileName}'. Ожидаемый путь: {sourcePath}",
                    sourcePath);
            }

            string template = await File.ReadAllTextAsync(sourcePath, cancellationToken)
                .ConfigureAwait(false);
            await File.WriteAllTextAsync(destinationPath, template, Encoding.UTF8, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task<string> ReadPromptAsync(
        string path,
        CancellationToken cancellationToken)
    {
        string content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidDataException($"Файл промпта пустой: {path}");

        if (content.Length > MaxPromptCharacters)
            throw new InvalidDataException(
                $"Файл промпта превышает ограничение {MaxPromptCharacters} символов: {path}");

        return content.Trim();
    }

    private static (string Content, PromptFeatures Features) ExtractFlags(string prompt)
    {
        int start = prompt.IndexOf(FlagsStart, StringComparison.Ordinal);
        if (start < 0)
            return (prompt, PromptFeatures.None);

        int contentStart = start + FlagsStart.Length;
        int end = prompt.IndexOf(FlagsEnd, contentStart, StringComparison.Ordinal);
        if (end < 0)
            throw new InvalidDataException("В SystemPrompt.md отсутствует закрывающий маркер [/flags].");

        if (prompt.IndexOf(FlagsStart, contentStart, StringComparison.Ordinal) >= 0)
            throw new InvalidDataException("В SystemPrompt.md допускается только один блок [flags].");

        PromptFeatures features = PromptFeatures.None;
        ReadOnlySpan<char> flagBlock = prompt.AsSpan(contentStart, end - contentStart);

        foreach (string rawLine in flagBlock.ToString().Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            if (line.Length < 3 || line[0] != '[' || line[^1] != ']')
                throw new InvalidDataException($"Некорректный флаг в SystemPrompt.md: '{line}'.");

            string name = line[1..^1];
            features |= name.ToLowerInvariant() switch
            {
                "memory" => PromptFeatures.Memory,
                "user-explanation" => PromptFeatures.UserExplanation,
                "protocol-output" => PromptFeatures.ProtocolOutput,
                _ => throw new InvalidDataException($"Неизвестный флаг промпта: [{name}].")
            };
        }

        string content = (prompt[..start] + prompt[(end + FlagsEnd.Length)..]).Trim();

        return (content, features);
    }
}
