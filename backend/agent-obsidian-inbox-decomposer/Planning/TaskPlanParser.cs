using System.Text.Json;
using System.Text.Json.Serialization;
using TaskSandbox.Obsidian;

namespace TaskSandbox.Planning;

/// <summary>Validates the model's planning JSON and assigns application-owned stable task IDs.</summary>
public static class TaskPlanParser
{
    private const int MaxResponseCharacters = 128 * 1024;
    private const int MaxSubtasks = 64;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        MaxDepth = 32
    };

    public static ObsidianTaskPlan Parse(
        string response,
        string parentTaskId,
        string workspaceId,
        string originalContentHash,
        DependencyPolicy dependencyPolicy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(response);
        ArgumentException.ThrowIfNullOrWhiteSpace(parentTaskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(originalContentHash);

        if (response.Length > MaxResponseCharacters)
            throw new InvalidDataException($"Ответ Decomposer превышает лимит {MaxResponseCharacters} символов.");

        string json = RemoveOptionalCodeFence(response);
        PlanDto dto = JsonSerializer.Deserialize<PlanDto>(json, JsonOptions)
            ?? throw new InvalidDataException("Decomposer вернул пустой JSON-объект.");

        string title = RequiredText(dto.Title, "title", 160, allowNewLines: false);
        string summary = RequiredText(dto.Summary, "summary", 6000, allowNewLines: true);
        if (dto.Subtasks is null || dto.Subtasks.Count is < 1 or > MaxSubtasks)
            throw new InvalidDataException($"Количество подзадач должно быть от 1 до {MaxSubtasks}.");

        var localKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (SubtaskDto subtask in dto.Subtasks)
        {
            if (subtask is null)
                throw new InvalidDataException("В массиве subtasks обнаружен пустой элемент.");

            string key = RequiredText(subtask.Key, "subtasks[].key", 16, allowNewLines: false);
            if (!IsSafeLocalKey(key))
                throw new InvalidDataException($"Недопустимый локальный key подзадачи: '{key}'.");
            if (!localKeys.Add(key))
                throw new InvalidDataException($"Повторяющийся локальный key: '{key}'.");
        }

        var generatedIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < dto.Subtasks.Count; i++)
        {
            string key = dto.Subtasks[i].Key!;
            string generatedId = $"{parentTaskId}-{i + 1:D2}";
            if (generatedId.Length > 80 || !IsSafeLocalKey(generatedId))
                throw new InvalidDataException("Не удалось сформировать допустимый ID подзадачи из ID родительской задачи.");
            generatedIds.Add(key, generatedId);
        }

        var subtasks = new List<ObsidianSubtaskDefinition>(dto.Subtasks.Count);
        for (int i = 0; i < dto.Subtasks.Count; i++)
        {
            SubtaskDto source = dto.Subtasks[i];
            string key = source.Key!;
            string taskTitle = RequiredText(source.Title, $"{key}.title", 160, allowNewLines: false);
            string description = RequiredText(source.Description, $"{key}.description", 6000, allowNewLines: true);
            string[] dependsOnKeys = ReadStringList(source.DependsOn, $"{key}.depends_on", 64, 80);
            string[] targetFiles = ReadStringList(source.TargetFiles, $"{key}.target_files", 32, 260);
            string[] requirements = ReadStringList(source.Requirements, $"{key}.requirements", 32, 1000);
            string[] acceptance = ReadStringList(source.AcceptanceCriteria, $"{key}.acceptance_criteria", 32, 1000);

            if (acceptance.Length == 0)
                throw new InvalidDataException($"Подзадача '{key}' должна содержать хотя бы один критерий приёмки.");

            var dependencies = new List<string>(dependsOnKeys.Length);
            foreach (string dependencyKey in dependsOnKeys)
            {
                if (!generatedIds.TryGetValue(dependencyKey, out string? dependencyId))
                    throw new InvalidDataException($"Подзадача '{key}' зависит от неизвестного key '{dependencyKey}'.");
                dependencies.Add(dependencyId);
            }

            ObsidianTaskPriority priority = ParsePriority(source.Priority, key);
            subtasks.Add(new ObsidianSubtaskDefinition(
                generatedIds[key],
                taskTitle,
                description,
                workspaceId,
                dependencies,
                targetFiles,
                requirements,
                acceptance,
                dependencyPolicy,
                priority));
        }

        string planMarkdown = BuildPlanMarkdown(title, summary, subtasks);
        return new ObsidianTaskPlan(parentTaskId, title, planMarkdown, subtasks, originalContentHash);
    }

    private static string RemoveOptionalCodeFence(string value)
    {
        string trimmed = value.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
            return trimmed;

        int firstLineEnd = trimmed.IndexOf('\n');
        if (firstLineEnd < 0)
            throw new InvalidDataException("Ответ содержит незавершённое Markdown-ограждение.");

        int closingFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        if (closingFence <= firstLineEnd || !string.IsNullOrWhiteSpace(trimmed[(closingFence + 3)..]))
            throw new InvalidDataException("Ответ содержит некорректное Markdown-ограждение.");

        return trimmed[(firstLineEnd + 1)..closingFence].Trim();
    }

    private static string RequiredText(string? value, string field, int maxLength, bool allowNewLines)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException($"В ответе Decomposer отсутствует обязательное поле '{field}'.");

        string text = value.Trim();
        if (text.Length > maxLength)
            throw new InvalidDataException($"Поле '{field}' превышает лимит {maxLength} символов.");

        foreach (char character in text)
        {
            if (character == '\0' || (char.IsControl(character) && !(allowNewLines && (character is '\r' or '\n' or '\t'))))
                throw new InvalidDataException($"Поле '{field}' содержит недопустимые управляющие символы.");
        }

        return text;
    }

    private static string[] ReadStringList(List<string?>? values, string field, int maxItems, int maxItemLength)
    {
        if (values is null)
            throw new InvalidDataException($"В ответе Decomposer отсутствует массив '{field}'.");
        if (values.Count > maxItems)
            throw new InvalidDataException($"Массив '{field}' превышает лимит {maxItems} элементов.");

        var result = new string[values.Count];
        for (int i = 0; i < values.Count; i++)
        {
            string item = RequiredText(values[i], $"{field}[{i}]", maxItemLength, allowNewLines: false);
            if (field.EndsWith("target_files", StringComparison.Ordinal))
                ValidateWorkspaceRelativePath(item, field);
            result[i] = item;
        }

        return result;
    }

    private static void ValidateWorkspaceRelativePath(string value, string field)
    {
        if (Path.IsPathRooted(value) || value.StartsWith('\\') || value.StartsWith('/') ||
            (value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':'))
        {
            throw new InvalidDataException($"'{field}' содержит абсолютный путь: '{value}'. Нужны пути относительно Workspace.");
        }

        string[] segments = value.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(static segment => segment is "." or ".."))
            throw new InvalidDataException($"'{field}' содержит небезопасный относительный путь: '{value}'.");
    }

    private static ObsidianTaskPriority ParsePriority(string? value, string key) => value?.ToLowerInvariant() switch
    {
        "low" => ObsidianTaskPriority.Low,
        "normal" => ObsidianTaskPriority.Normal,
        "high" => ObsidianTaskPriority.High,
        "critical" => ObsidianTaskPriority.Critical,
        _ => throw new InvalidDataException($"Подзадача '{key}' имеет неизвестный priority: '{value}'.")
    };

    private static bool IsSafeLocalKey(string value)
    {
        if (value.Length is < 1 or > 80 || value is "." or "..")
            return false;

        foreach (char character in value)
        {
            if (!(char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
                return false;
        }

        return true;
    }

    private static string BuildPlanMarkdown(
        string title,
        string summary,
        IReadOnlyList<ObsidianSubtaskDefinition> subtasks)
    {
        var builder = new System.Text.StringBuilder(1024);
        builder.Append("# ").AppendLine(title).AppendLine()
            .AppendLine($"**Dependency policy:** `{FormatDependencyPolicy(subtasks[0].DependencyPolicy)}`").AppendLine()
            .AppendLine("## План").AppendLine()
            .AppendLine(summary).AppendLine()
            .AppendLine("## Подзадачи").AppendLine();

        foreach (ObsidianSubtaskDefinition task in subtasks)
        {
            builder.Append("### ").Append(task.Id).Append(" — ").AppendLine(task.Title).AppendLine()
                .AppendLine(task.Description).AppendLine();

            if (task.DependsOn.Count > 0)
                builder.Append("Зависимости: ").AppendLine(string.Join(", ", task.DependsOn));
            if (task.TargetFiles.Count > 0)
                builder.Append("Целевые файлы: ").AppendLine(string.Join(", ", task.TargetFiles));

            builder.Append("Политика зависимостей: `").Append(FormatDependencyPolicy(task.DependencyPolicy)).AppendLine("`");
            AppendList(builder, "Требования", task.Requirements);
            AppendList(builder, "Критерии приёмки", task.AcceptanceCriteria);
        }

        return builder.ToString();
    }

    private static string FormatDependencyPolicy(DependencyPolicy policy) => policy switch
    {
        DependencyPolicy.ExistingOnly => "existing_only",
        DependencyPolicy.AllowIfNecessary => "allow_if_necessary",
        DependencyPolicy.Unrestricted => "unrestricted",
        _ => throw new InvalidDataException($"Неизвестная политика зависимостей: {policy}.")
    };

    private static void AppendList(System.Text.StringBuilder builder, string heading, IReadOnlyList<string> values)
    {
        builder.Append("**").Append(heading).AppendLine("**");
        foreach (string value in values)
            builder.Append("- ").AppendLine(value);
        builder.AppendLine();
    }

    private sealed class PlanDto
    {
        public PlanDto() { }
        [JsonPropertyName("title")]
        public string? Title { get; set; }
        [JsonPropertyName("summary")]
        public string? Summary { get; set; }
        [JsonPropertyName("subtasks")]
        public List<SubtaskDto>? Subtasks { get; set; }
    }

    private sealed class SubtaskDto
    {
        public SubtaskDto() { }
        [JsonPropertyName("key")]
        public string? Key { get; set; }
        [JsonPropertyName("title")]
        public string? Title { get; set; }
        [JsonPropertyName("description")]
        public string? Description { get; set; }
        [JsonPropertyName("depends_on")]
        public List<string?>? DependsOn { get; set; }
        [JsonPropertyName("target_files")]
        public List<string?>? TargetFiles { get; set; }
        [JsonPropertyName("requirements")]
        public List<string?>? Requirements { get; set; }
        [JsonPropertyName("acceptance_criteria")]
        public List<string?>? AcceptanceCriteria { get; set; }
        [JsonPropertyName("priority")]
        public string? Priority { get; set; }
    }
}
