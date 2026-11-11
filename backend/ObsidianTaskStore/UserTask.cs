using System.Text;
using System.Text.Json;
using TaskSandbox.Prompting;

namespace TaskSandbox;

public enum TaskStatus : byte
{
    Proceed,
    InProgress,
    Ready,
    Rejected,
    Failed,
    Cancelled
}

public enum TaskValidationResult : byte
{
    Success,
    EmptyPath,
    InvalidPath
}

public sealed class UserTask
{
    public int Id { get; init; }
    public string Payload { get; init; } = string.Empty;
    public string WorkDir { get; init; } = string.Empty;
    public required PromptSet Prompts { get; init; }
    public string ProjectMemoryMarkdown { get; init; } = string.Empty;
    public string TargetFiles { get; init; } = "Не заданы; подходящие файлы необходимо определить после анализа.";
    public string Requirements { get; init; } = "Дополнительные требования не заданы.";
    public string CurrentProgress { get; init; } = "Задача ещё не начата.";
    public string CompletedWork { get; init; } = "В рамках текущей задачи пока ничего не выполнено.";
    public TaskStatus Status { get; private set; } = TaskStatus.Proceed;

    public async Task RunAsync(
        IModelProvider modelProvider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modelProvider);
        ArgumentNullException.ThrowIfNull(Prompts);

        TaskValidationResult validation = ValidateWorkDirectory(WorkDir);
        if (validation != TaskValidationResult.Success)
        {
            Status = TaskStatus.Rejected;
            Console.WriteLine($"[Task {Id}] Задача отклонена: {GetValidationMessage(validation)}");
            return;
        }

        Status = TaskStatus.InProgress;
        string filePath = Path.Combine(WorkDir, $"task_{Id}.txt");

        try
        {
            Directory.CreateDirectory(WorkDir);
            await File.WriteAllTextAsync(
                filePath,
                $"--- ЗАДАЧА ---{Environment.NewLine}{Payload}{Environment.NewLine}{Environment.NewLine}",
                cancellationToken).ConfigureAwait(false);

            Console.WriteLine($"[Task {Id}] Потоковый запрос к AI Backend:");

            TaskPromptContext taskContext = new(
                TaskId: Id,
                WorkspacePath: WorkDir,
                Description: Payload,
                TargetFiles: TargetFiles,
                Requirements: Requirements,
                CurrentProgress: CurrentProgress,
                CompletedWork: CompletedWork);

            ModelMessage[] messages = AgentPromptBuilder.BuildTaskMessages(
                Prompts,
                ProjectMemoryMarkdown,
                taskContext);

            var request = new ModelRequest(messages, Temperature: 0.7f);

            var answer = new StringBuilder(1024);

            await modelProvider.StreamAsync(
                request,
                content =>
                {
                    answer.Append(content);
                    Console.Write(content);
                    return ValueTask.CompletedTask;
                },
                cancellationToken).ConfigureAwait(false);

            string answerText = answer.ToString();
            if (string.IsNullOrWhiteSpace(answerText))
                throw new InvalidDataException("The model backend returned an empty completion.");

            await File.AppendAllTextAsync(
                filePath,
                $"{Environment.NewLine}--- ОТВЕТ МОДЕЛИ ---{Environment.NewLine}{answerText}{Environment.NewLine}",
                cancellationToken).ConfigureAwait(false);

            Console.WriteLine($"{Environment.NewLine}[Task {Id}] Выполнена.");
            Status = TaskStatus.Ready;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Status = TaskStatus.Cancelled;
            Console.WriteLine($"[Task {Id}] Отменена.");
            throw;
        }
        catch (TimeoutException ex)
        {
            Status = TaskStatus.Failed;
            Console.WriteLine($"[Task {Id}] Тайм-аут AI Backend: {ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            Status = TaskStatus.Failed;
            Console.WriteLine($"[Task {Id}] Ошибка HTTP: {ex.Message}");
        }
        catch (JsonException ex)
        {
            Status = TaskStatus.Failed;
            Console.WriteLine($"[Task {Id}] Некорректный JSON от AI Backend: {ex.Message}");
        }
        catch (InvalidDataException ex)
        {
            Status = TaskStatus.Failed;
            Console.WriteLine($"[Task {Id}] Некорректный ответ AI Backend: {ex.Message}");
        }
        catch (IOException ex)
        {
            Status = TaskStatus.Failed;
            Console.WriteLine($"[Task {Id}] Ошибка файловой системы: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Status = TaskStatus.Failed;
            Console.WriteLine($"[Task {Id}] Недостаточно прав: {ex.Message}");
        }
    }

    private static TaskValidationResult ValidateWorkDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return TaskValidationResult.EmptyPath;

        try
        {
            _ = Path.GetFullPath(path);
            return TaskValidationResult.Success;
        }
        catch (ArgumentException)
        {
            return TaskValidationResult.InvalidPath;
        }
        catch (NotSupportedException)
        {
            return TaskValidationResult.InvalidPath;
        }
        catch (PathTooLongException)
        {
            return TaskValidationResult.InvalidPath;
        }
    }

    private static string GetValidationMessage(TaskValidationResult result) => result switch
    {
        TaskValidationResult.EmptyPath => "Путь к рабочей директории пустой.",
        TaskValidationResult.InvalidPath => "Некорректный путь к рабочей директории.",
        _ => string.Empty
    };
}
