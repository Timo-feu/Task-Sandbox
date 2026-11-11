using System.Text;

namespace TaskSandbox.Obsidian;

/// <summary>
/// File-system integration with an Obsidian vault. The store owns only the configured
/// LocalCodingAgent folder and never grants model code arbitrary vault access.
/// </summary>
public sealed class ObsidianTaskStore
{
    private const string InboxFolder = "Inbox";
    private const string PlanningFolder = "Planning";
    private const string QueueFolder = "Queue";
    private const string InProgressFolder = "InProgress";
    private const string CompletedFolder = "Completed";
    private const string FailedFolder = "Failed";
    private const string ProjectsFolder = "Projects";
    private const string ReportsFolder = "Reports";
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly string _vaultPath;
    private readonly string _agentRoot;
    private readonly int _maxMarkdownFileBytes;

    public ObsidianTaskStore(ObsidianTaskStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.VaultPath))
            throw new ArgumentException("VaultPath must be configured.", nameof(options));
        if (!IsSafeSegment(options.AgentFolderName))
            throw new ArgumentException("AgentFolderName must be a single safe directory name.", nameof(options));
        if (options.MaxMarkdownFileBytes < 1024)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxMarkdownFileBytes must be at least 1024 bytes.");

        _vaultPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.VaultPath));
        _agentRoot = Path.Combine(_vaultPath, options.AgentFolderName);
        _maxMarkdownFileBytes = options.MaxMarkdownFileBytes;
    }

    public string VaultPath => _vaultPath;
    public string AgentRootPath => _agentRoot;

    public string GetProjectMemoryPath(string workspaceId)
    {
        ValidateId(workspaceId, nameof(workspaceId));
        return Path.Combine(_agentRoot, ProjectsFolder, workspaceId, "ProjectMemory.md");
    }

    /// <summary>Creates the managed folder structure. Existing notes are never overwritten.</summary>
    public void EnsureInitialized()
    {
        Directory.CreateDirectory(_vaultPath);
        Directory.CreateDirectory(_agentRoot);
        EnsureNoReparsePoints(_agentRoot);

        foreach (string folder in new[]
        {
            InboxFolder, PlanningFolder, QueueFolder, InProgressFolder,
            CompletedFolder, FailedFolder, ProjectsFolder, ReportsFolder
        })
        {
            string path = Path.Combine(_agentRoot, folder);
            Directory.CreateDirectory(path);
            EnsureNoReparsePoints(path);
        }
    }

    public async Task<IReadOnlyList<ObsidianTaskDocument>> ReadInboxAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        string inboxPath = GetManagedDirectory(InboxFolder);
        var result = new List<ObsidianTaskDocument>();

        foreach (string filePath in Directory.EnumerateFiles(inboxPath, "*.md", SearchOption.TopDirectoryOnly)
                     .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureNoReparsePoints(filePath);
            string markdown = await ReadMarkdownAsync(filePath, cancellationToken).ConfigureAwait(false);
            result.Add(ParseTask(markdown, filePath, fallbackId: Path.GetFileNameWithoutExtension(filePath)));
        }

        return result;
    }

    /// <summary>
    /// Moves one Inbox note into Planning/&lt;task-id&gt;/Original.md. If the note has no id,
    /// a stable id is generated and written into its Frontmatter.
    /// </summary>
    public async Task<ObsidianTaskDocument> MoveInboxTaskToPlanningAsync(
        string inboxFilePath,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(inboxFilePath);

        string sourcePath = Path.GetFullPath(inboxFilePath);
        EnsureDirectChildOf(sourcePath, GetManagedDirectory(InboxFolder));
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Inbox task was not found.", sourcePath);

        EnsureNoReparsePoints(sourcePath);
        string markdown = await ReadMarkdownAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        MarkdownFrontmatter frontmatter = MarkdownFrontmatter.Parse(markdown);

        string id = frontmatter.GetValue("id");
        if (string.IsNullOrWhiteSpace(id))
            id = CreateTaskId();
        ValidateId(id, "id");

        string destinationDirectory = Path.Combine(GetManagedDirectory(PlanningFolder), id);
        string destinationPath = Path.Combine(destinationDirectory, "Original.md");
        if (Directory.Exists(destinationDirectory) || File.Exists(destinationPath))
            throw new IOException($"Задача '{id}' уже существует в Planning.");

        Directory.CreateDirectory(destinationDirectory);
        EnsureNoReparsePoints(destinationDirectory);

        try
        {
            File.Move(sourcePath, destinationPath);
            string movedMarkdown = await ReadMarkdownAsync(destinationPath, cancellationToken).ConfigureAwait(false);
            frontmatter = MarkdownFrontmatter.Parse(movedMarkdown);
            frontmatter.SetValue("id", id);
            frontmatter.SetValue("type", "request");
            frontmatter.SetValue("status", "planning");
            await WriteMarkdownAtomicAsync(destinationPath, frontmatter.ToMarkdown(), cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            if (Directory.Exists(destinationDirectory) &&
                !Directory.EnumerateFileSystemEntries(destinationDirectory).Any())
            {
                Directory.Delete(destinationDirectory);
            }

            throw;
        }

        string finalMarkdown = await ReadMarkdownAsync(destinationPath, cancellationToken).ConfigureAwait(false);
        return ParseTask(finalMarkdown, destinationPath, id);
    }

    /// <summary>
    /// Saves a human-readable plan and publishes all generated subtasks as one directory move.
    /// Queue readers will not observe a partially written plan directory.
    /// </summary>
    public async Task PublishPlanAsync(
        ObsidianTaskPlan plan,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(plan);
        ValidateId(plan.ParentTaskId, nameof(plan.ParentTaskId));
        ArgumentException.ThrowIfNullOrWhiteSpace(plan.Title);
        ArgumentNullException.ThrowIfNull(plan.PlanMarkdown);
        ArgumentNullException.ThrowIfNull(plan.Subtasks);

        if (plan.Subtasks.Count == 0)
            throw new InvalidDataException("Decomposer должен создать хотя бы одну подзадачу.");
        if (plan.Subtasks.Count > 128)
            throw new InvalidDataException("Один план не может содержать более 128 подзадач.");

        string planningDirectory = Path.Combine(GetManagedDirectory(PlanningFolder), plan.ParentTaskId);
        EnsureExistingManagedDirectory(planningDirectory, PlanningFolder, plan.ParentTaskId);
        string originalPath = Path.Combine(planningDirectory, "Original.md");
        if (!File.Exists(originalPath))
            throw new FileNotFoundException("Исходная задача не найдена в Planning.", originalPath);

        string originalMarkdown = await ReadMarkdownAsync(originalPath, cancellationToken).ConfigureAwait(false);
        ObsidianTaskDocument originalTask = ParseTask(originalMarkdown, originalPath, plan.ParentTaskId);
        if (!string.Equals(originalTask.Id, plan.ParentTaskId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("ParentTaskId не совпадает с ID исходной Inbox-задачи.");

        ValidatePlan(plan, originalTask.WorkspaceId);

        string queueRoot = GetManagedDirectory(QueueFolder);
        string publishedDirectory = Path.Combine(queueRoot, plan.ParentTaskId);
        if (Directory.Exists(publishedDirectory) || File.Exists(publishedDirectory))
            throw new IOException($"Очередь для задачи '{plan.ParentTaskId}' уже существует.");

        string planPath = Path.Combine(planningDirectory, "Plan.md");
        await WriteMarkdownAtomicAsync(planPath, plan.PlanMarkdown, cancellationToken).ConfigureAwait(false);

        string stagingDirectory = Path.Combine(queueRoot, ".staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDirectory);

        try
        {
            foreach (ObsidianSubtaskDefinition subtask in plan.Subtasks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string taskMarkdown = CreateSubtaskMarkdown(plan.ParentTaskId, subtask);
                string taskPath = Path.Combine(stagingDirectory, subtask.Id + ".md");
                await WriteMarkdownAtomicAsync(taskPath, taskMarkdown, cancellationToken).ConfigureAwait(false);
            }

            Directory.Move(stagingDirectory, publishedDirectory);

            // The queue directory move is the publication commit. Re-read Original.md
            // so manual Obsidian edits made during planning are not overwritten.
            string latestOriginal = await ReadMarkdownAsync(originalPath, CancellationToken.None).ConfigureAwait(false);
            MarkdownFrontmatter parentFrontmatter = MarkdownFrontmatter.Parse(latestOriginal);
            parentFrontmatter.SetValue("status", "queued");
            parentFrontmatter.SetValue("subtask_count", plan.Subtasks.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await WriteMarkdownAtomicAsync(originalPath, parentFrontmatter.ToMarkdown(), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            if (Directory.Exists(stagingDirectory))
                Directory.Delete(stagingDirectory, recursive: true);
            throw;
        }
    }

    /// <summary>
    /// Returns unfinished tasks left in InProgress after an unexpected shutdown to Queue.
    /// Call once during application startup, before starting the single Consumer.
    /// </summary>
    public async Task<int> RecoverInProgressTasksAsync(CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        string inProgressRoot = GetManagedDirectory(InProgressFolder);
        string queueRoot = GetManagedDirectory(QueueFolder);
        int recoveredCount = 0;

        foreach (string sourcePath in Directory.EnumerateFiles(inProgressRoot, "*.md", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string markdown = await ReadMarkdownAsync(sourcePath, cancellationToken).ConfigureAwait(false);
            MarkdownFrontmatter frontmatter = MarkdownFrontmatter.Parse(markdown);
            string taskId = frontmatter.GetValue("id", Path.GetFileNameWithoutExtension(sourcePath));
            ValidateId(taskId, "id");
            string parentId = frontmatter.GetValue("parent", taskId);
            ValidateId(parentId, "parent");

            string queueDirectory = Path.Combine(queueRoot, parentId);
            Directory.CreateDirectory(queueDirectory);
            EnsureNoReparsePoints(queueDirectory);
            string destinationPath = Path.Combine(queueDirectory, taskId + ".md");
            if (File.Exists(destinationPath))
                throw new IOException($"Нельзя восстановить '{taskId}': такая задача уже есть в Queue.");

            frontmatter.SetValue("status", "pending");
            await WriteMarkdownAtomicAsync(sourcePath, frontmatter.ToMarkdown(), cancellationToken)
                .ConfigureAwait(false);
            File.Move(sourcePath, destinationPath);
            recoveredCount++;
        }

        return recoveredCount;
    }

    /// <summary>Claims the highest-priority queued task whose dependencies are completed.</summary>
    public async Task<ObsidianTaskDocument?> ClaimNextReadyTaskAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        string queueRoot = GetManagedDirectory(QueueFolder);
        HashSet<string> completedIds = await ReadIdsAsync(
            GetManagedDirectory(CompletedFolder), cancellationToken).ConfigureAwait(false);

        var candidates = new List<ObsidianTaskDocument>();
        foreach (string taskPath in EnumerateQueuedTaskFiles(queueRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureNoReparsePoints(taskPath);
            string markdown = await ReadMarkdownAsync(taskPath, cancellationToken).ConfigureAwait(false);
            ObsidianTaskDocument task = ParseTask(markdown, taskPath, Path.GetFileNameWithoutExtension(taskPath));
            if (task.Status is "pending" or "queued" or "")
                candidates.Add(task);
        }

        candidates.Sort(static (left, right) =>
        {
            int priority = right.Priority.CompareTo(left.Priority);
            if (priority != 0)
                return priority;

            int title = string.Compare(left.Title, right.Title, StringComparison.OrdinalIgnoreCase);
            return title != 0 ? title : string.Compare(left.Id, right.Id, StringComparison.OrdinalIgnoreCase);
        });

        string inProgressRoot = GetManagedDirectory(InProgressFolder);
        foreach (ObsidianTaskDocument candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (candidate.DependsOn.Any(dependency => !completedIds.Contains(dependency)))
                continue;

            string destinationPath = Path.Combine(inProgressRoot, candidate.Id + ".md");
            if (File.Exists(destinationPath))
                throw new IOException($"Задача '{candidate.Id}' уже находится в InProgress.");

            string markdown = await ReadMarkdownAsync(candidate.SourcePath, cancellationToken).ConfigureAwait(false);
            MarkdownFrontmatter frontmatter = MarkdownFrontmatter.Parse(markdown);
            frontmatter.SetValue("status", "in_progress");
            int attempt = int.TryParse(
                frontmatter.GetValue("attempt", "0"),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out int parsedAttempt)
                ? Math.Max(0, parsedAttempt)
                : 0;
            frontmatter.SetValue("attempt", checked(attempt + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            await WriteMarkdownAtomicAsync(candidate.SourcePath, frontmatter.ToMarkdown(), cancellationToken)
                .ConfigureAwait(false);

            try
            {
                File.Move(candidate.SourcePath, destinationPath);
            }
            catch (FileNotFoundException)
            {
                // Another local worker claimed this exact file after our scan. Try the next candidate.
                continue;
            }

            string claimedMarkdown = await ReadMarkdownAsync(destinationPath, cancellationToken).ConfigureAwait(false);
            return ParseTask(claimedMarkdown, destinationPath, candidate.Id);
        }

        return null;
    }

    public async Task CompleteTaskAsync(
        string taskId,
        string reportMarkdown,
        CancellationToken cancellationToken = default)
    {
        await FinishTaskAsync(taskId, reportMarkdown, ObsidianTaskState.Completed, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task FailTaskAsync(
        string taskId,
        string failureReportMarkdown,
        CancellationToken cancellationToken = default)
    {
        await FinishTaskAsync(taskId, failureReportMarkdown, ObsidianTaskState.Failed, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task RequeueFailedTaskAsync(
        string taskId,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        ValidateId(taskId, nameof(taskId));
        string sourcePath = Path.Combine(GetManagedDirectory(FailedFolder), taskId + ".md");
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException($"Failed task '{taskId}' was not found.", sourcePath);

        string markdown = await ReadMarkdownAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        MarkdownFrontmatter frontmatter = MarkdownFrontmatter.Parse(markdown);
        string parentId = frontmatter.GetValue("parent", taskId);
        ValidateId(parentId, "parent");
        frontmatter.SetValue("status", "pending");

        string queueDirectory = Path.Combine(GetManagedDirectory(QueueFolder), parentId);
        Directory.CreateDirectory(queueDirectory);
        EnsureNoReparsePoints(queueDirectory);
        string destinationPath = Path.Combine(queueDirectory, taskId + ".md");
        if (File.Exists(destinationPath))
            throw new IOException($"Задача '{taskId}' уже присутствует в Queue.");

        await WriteMarkdownAtomicAsync(sourcePath, frontmatter.ToMarkdown(), cancellationToken).ConfigureAwait(false);
        File.Move(sourcePath, destinationPath);
    }

    public async Task WriteReportAsync(
        string parentTaskId,
        string reportName,
        string markdown,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        ValidateId(parentTaskId, nameof(parentTaskId));
        ValidateId(reportName, nameof(reportName));
        ArgumentNullException.ThrowIfNull(markdown);

        string reportDirectory = Path.Combine(GetManagedDirectory(ReportsFolder), parentTaskId);
        Directory.CreateDirectory(reportDirectory);
        EnsureNoReparsePoints(reportDirectory);
        string reportPath = Path.Combine(reportDirectory, reportName + ".md");
        if (File.Exists(reportPath))
            throw new IOException($"Отчёт уже существует: {reportPath}");
        await WriteMarkdownAtomicAsync(reportPath, markdown, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> LoadProjectMemoryAsync(
        string workspaceId,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        ValidateId(workspaceId, nameof(workspaceId));
        string memoryPath = GetProjectMemoryPath(workspaceId);

        if (File.Exists(memoryPath))
            return await ReadMarkdownAsync(memoryPath, cancellationToken).ConfigureAwait(false);

        string directory = Path.GetDirectoryName(memoryPath)!;
        Directory.CreateDirectory(directory);
        EnsureNoReparsePoints(directory);
        string initialMemory = CreateEmptyProjectMemory(workspaceId);
        await WriteMarkdownAtomicAsync(memoryPath, initialMemory, cancellationToken).ConfigureAwait(false);
        return initialMemory;
    }

    public async Task SaveProjectMemoryAsync(
        string workspaceId,
        string markdown,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        ValidateId(workspaceId, nameof(workspaceId));
        ArgumentNullException.ThrowIfNull(markdown);
        string memoryPath = GetProjectMemoryPath(workspaceId);
        string directory = Path.GetDirectoryName(memoryPath)!;
        Directory.CreateDirectory(directory);
        EnsureNoReparsePoints(directory);
        await WriteMarkdownAtomicAsync(memoryPath, markdown, cancellationToken).ConfigureAwait(false);
    }

    private async Task FinishTaskAsync(
        string taskId,
        string reportMarkdown,
        ObsidianTaskState targetState,
        CancellationToken cancellationToken)
    {
        EnsureInitialized();
        ValidateId(taskId, nameof(taskId));
        ArgumentNullException.ThrowIfNull(reportMarkdown);

        string inProgressPath = Path.Combine(GetManagedDirectory(InProgressFolder), taskId + ".md");
        if (!File.Exists(inProgressPath))
            throw new FileNotFoundException($"Задача '{taskId}' не найдена в InProgress.", inProgressPath);

        string markdown = await ReadMarkdownAsync(inProgressPath, cancellationToken).ConfigureAwait(false);
        MarkdownFrontmatter frontmatter = MarkdownFrontmatter.Parse(markdown);
        string parentId = frontmatter.GetValue("parent", taskId);
        ValidateId(parentId, "parent");

        string targetFolder = targetState == ObsidianTaskState.Completed ? CompletedFolder : FailedFolder;
        string destinationPath = Path.Combine(GetManagedDirectory(targetFolder), taskId + ".md");
        if (File.Exists(destinationPath))
            throw new IOException($"Файл задачи '{taskId}' уже существует в {targetFolder}.");

        string reportName = targetState == ObsidianTaskState.Completed ? "Step-" + taskId : "Failure-" + taskId;
        await WriteReportAsync(parentId, reportName, reportMarkdown, cancellationToken).ConfigureAwait(false);

        frontmatter.SetValue("status", targetState == ObsidianTaskState.Completed ? "completed" : "failed");
        await WriteMarkdownAtomicAsync(inProgressPath, frontmatter.ToMarkdown(), cancellationToken).ConfigureAwait(false);
        File.Move(inProgressPath, destinationPath);
    }

    private async Task<HashSet<string>> ReadIdsAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureNoReparsePoints(path);
            string markdown = await ReadMarkdownAsync(path, cancellationToken).ConfigureAwait(false);
            MarkdownFrontmatter frontmatter = MarkdownFrontmatter.Parse(markdown);
            string id = frontmatter.GetValue("id", Path.GetFileNameWithoutExtension(path));
            if (IsSafeSegment(id))
                result.Add(id);
        }

        return result;
    }

    private IEnumerable<string> EnumerateQueuedTaskFiles(string queueRoot)
    {
        foreach (string directory in Directory.EnumerateDirectories(queueRoot, "*", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileName(directory);
            if (name.StartsWith(".", StringComparison.Ordinal))
                continue;

            EnsureNoReparsePoints(directory);
            foreach (string file in Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly))
                yield return file;
        }
    }

    private static ObsidianTaskDocument ParseTask(string markdown, string sourcePath, string fallbackId)
    {
        MarkdownFrontmatter frontmatter = MarkdownFrontmatter.Parse(markdown);
        string id = frontmatter.GetValue("id", fallbackId);
        string title = frontmatter.GetValue("title");
        if (string.IsNullOrWhiteSpace(title))
            title = FindFirstHeading(frontmatter.Body) ?? Path.GetFileNameWithoutExtension(sourcePath);

        string priorityValue = frontmatter.GetValue("priority", "normal");
        ObsidianTaskPriority priority = priorityValue.ToLowerInvariant() switch
        {
            "critical" => ObsidianTaskPriority.Critical,
            "high" => ObsidianTaskPriority.High,
            "low" => ObsidianTaskPriority.Low,
            _ => ObsidianTaskPriority.Normal
        };

        return new ObsidianTaskDocument(
            id,
            title,
            frontmatter.Body,
            NullIfEmpty(frontmatter.GetValue("workspace")),
            NullIfEmpty(frontmatter.GetValue("parent")),
            priority,
            frontmatter.GetList("depends_on"),
            frontmatter.GetList("target_files"),
            frontmatter.GetList("requirements"),
            frontmatter.GetList("acceptance_criteria"),
            frontmatter.GetValue("status", "pending"),
            sourcePath);
    }

    private static string CreateSubtaskMarkdown(string parentTaskId, ObsidianSubtaskDefinition task)
    {
        var frontmatter = new MarkdownFrontmatterBuilder();
        frontmatter.Add("id", task.Id);
        frontmatter.Add("type", "subtask");
        frontmatter.Add("parent", parentTaskId);
        frontmatter.Add("workspace", task.WorkspaceId);
        frontmatter.Add("title", task.Title);
        frontmatter.Add("priority", task.Priority.ToString().ToLowerInvariant());
        frontmatter.Add("status", "pending");
        frontmatter.Add("attempt", "0");
        frontmatter.Add("created_utc", DateTimeOffset.UtcNow.ToString("O"));
        frontmatter.AddList("depends_on", task.DependsOn);
        frontmatter.AddList("target_files", task.TargetFiles);
        frontmatter.AddList("requirements", task.Requirements);
        frontmatter.AddList("acceptance_criteria", task.AcceptanceCriteria);

        var body = new StringBuilder(512);
        body.Append("# ").AppendLine(task.Title).AppendLine();
        body.AppendLine("## Description").AppendLine().AppendLine(task.Description.Trim()).AppendLine();
        AppendBulletSection(body, "Requirements", task.Requirements);
        AppendBulletSection(body, "Target files", task.TargetFiles);
        AppendBulletSection(body, "Acceptance criteria", task.AcceptanceCriteria);
        return frontmatter.ToMarkdown(body.ToString());
    }

    private static void AppendBulletSection(StringBuilder builder, string title, IReadOnlyList<string> items)
    {
        builder.Append("## ").AppendLine(title).AppendLine();
        if (items.Count == 0)
        {
            builder.AppendLine("- Not specified").AppendLine();
            return;
        }

        foreach (string item in items)
            builder.Append("- ").AppendLine(item);
        builder.AppendLine();
    }

    private static void ValidatePlan(ObsidianTaskPlan plan, string? originalWorkspaceId)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? planWorkspaceId = originalWorkspaceId;
        foreach (ObsidianSubtaskDefinition task in plan.Subtasks)
        {
            ArgumentNullException.ThrowIfNull(task);
            ValidateId(task.Id, "subtask.Id");
            ValidateId(task.WorkspaceId, "subtask.WorkspaceId");
            if (planWorkspaceId is null)
                planWorkspaceId = task.WorkspaceId;
            else if (!string.Equals(task.WorkspaceId, planWorkspaceId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Все подзадачи одного плана должны относиться к одному Workspace.");
            ArgumentNullException.ThrowIfNull(task.DependsOn);
            ArgumentNullException.ThrowIfNull(task.TargetFiles);
            ArgumentNullException.ThrowIfNull(task.Requirements);
            ArgumentNullException.ThrowIfNull(task.AcceptanceCriteria);
            if (!Enum.IsDefined(task.Priority))
                throw new InvalidDataException($"Недопустимый Priority у подзадачи '{task.Id}'.");
            ArgumentException.ThrowIfNullOrWhiteSpace(task.Title);
            ArgumentNullException.ThrowIfNull(task.Description);
            if (string.Equals(task.Id, plan.ParentTaskId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("ID подзадачи не может совпадать с ID родительской задачи.");
            if (!ids.Add(task.Id))
                throw new InvalidDataException($"Повторяющийся ID подзадачи: '{task.Id}'.");
        }

        var indegree = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var outgoing = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (ObsidianSubtaskDefinition task in plan.Subtasks)
        {
            indegree[task.Id] = 0;
            outgoing[task.Id] = [];
        }

        foreach (ObsidianSubtaskDefinition task in plan.Subtasks)
        {
            var uniqueDependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string dependency in task.DependsOn)
            {
                ValidateId(dependency, "depends_on");
                if (!uniqueDependencies.Add(dependency))
                    throw new InvalidDataException($"Подзадача '{task.Id}' содержит повторную зависимость '{dependency}'.");
                if (!ids.Contains(dependency))
                    throw new InvalidDataException($"Подзадача '{task.Id}' зависит от неизвестной подзадачи '{dependency}'.");
                if (string.Equals(task.Id, dependency, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Подзадача '{task.Id}' зависит сама от себя.");

                outgoing[dependency].Add(task.Id);
                indegree[task.Id]++;
            }
        }

        var ready = new Queue<string>(indegree.Where(static pair => pair.Value == 0).Select(static pair => pair.Key));
        int visited = 0;
        while (ready.TryDequeue(out string? current))
        {
            visited++;
            foreach (string dependent in outgoing[current])
            {
                int remaining = --indegree[dependent];
                if (remaining == 0)
                    ready.Enqueue(dependent);
            }
        }

        if (visited != plan.Subtasks.Count)
            throw new InvalidDataException("В плане обнаружена циклическая зависимость между подзадачами.");
    }

    private string GetManagedDirectory(string folderName) => Path.Combine(_agentRoot, folderName);

    private void EnsureExistingManagedDirectory(string path, string topLevelFolder, string childName)
    {
        string expected = Path.Combine(GetManagedDirectory(topLevelFolder), childName);
        if (!PathsEqual(path, expected) || !Directory.Exists(path))
            throw new DirectoryNotFoundException($"Ожидаемая директория не найдена: {expected}");
        EnsureNoReparsePoints(path);
    }

    private static void EnsureDirectChildOf(string filePath, string directory)
    {
        string fullDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        string? parent = Path.GetDirectoryName(filePath);
        if (parent is null || !PathsEqual(parent, fullDirectory) ||
            !string.Equals(Path.GetExtension(filePath), ".md", StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Разрешено перемещать только Markdown-файлы непосредственно из Inbox.");
        }
    }

    private void EnsureNoReparsePoints(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string relative = Path.GetRelativePath(_agentRoot, fullPath);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
        {
            throw new UnauthorizedAccessException("Путь выходит за пределы управляемой папки Obsidian.");
        }

        CheckReparsePoint(_agentRoot);
        string current = _agentRoot;
        if (relative == ".")
            return;

        string[] segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        foreach (string segment in segments)
        {
            current = Path.Combine(current, segment);
            if (File.Exists(current) || Directory.Exists(current))
                CheckReparsePoint(current);
        }
    }

    private static void CheckReparsePoint(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException($"Символические ссылки и reparse points запрещены в управляемой папке: {path}");
    }

    private async Task<string> ReadMarkdownAsync(string path, CancellationToken cancellationToken)
    {
        EnsureNoReparsePoints(path);
        var info = new FileInfo(path);
        if (info.Length > _maxMarkdownFileBytes)
            throw new InvalidDataException($"Markdown-файл превышает лимит {_maxMarkdownFileBytes} байт: {path}");

        return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteMarkdownAtomicAsync(
        string destinationPath,
        string markdown,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        if (Utf8NoBom.GetByteCount(markdown) > _maxMarkdownFileBytes)
            throw new InvalidDataException($"Markdown-файл превышает лимит {_maxMarkdownFileBytes} байт: {destinationPath}");

        string directory = Path.GetDirectoryName(destinationPath)!;
        Directory.CreateDirectory(directory);
        EnsureNoReparsePoints(directory);
        if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
            EnsureNoReparsePoints(destinationPath);
        string temporaryPath = Path.Combine(directory, ".tmp-" + Guid.NewGuid().ToString("N"));

        try
        {
            await File.WriteAllTextAsync(temporaryPath, markdown, Utf8NoBom, cancellationToken)
                .ConfigureAwait(false);
            EnsureNoReparsePoints(temporaryPath);
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static string CreateEmptyProjectMemory(string workspaceId) =>
        $"# Project Memory{Environment.NewLine}" +
        $"> Workspace: `{workspaceId}`{Environment.NewLine}" +
        "> Facts must be verified against current project files when needed." + Environment.NewLine + Environment.NewLine +
        "## Desired Outcome" + Environment.NewLine + "_Not recorded yet._" + Environment.NewLine + Environment.NewLine +
        "## Current State" + Environment.NewLine + "_Not analyzed yet._" + Environment.NewLine + Environment.NewLine +
        "## Completed" + Environment.NewLine + "_Nothing verified yet._" + Environment.NewLine + Environment.NewLine +
        "## Next Steps" + Environment.NewLine + "_Not determined yet._" + Environment.NewLine + Environment.NewLine +
        "## Architecture and Technologies" + Environment.NewLine + "_Not analyzed yet._" + Environment.NewLine + Environment.NewLine +
        "## Important Files" + Environment.NewLine + "_Not analyzed yet._" + Environment.NewLine + Environment.NewLine +
        "## Constraints and Decisions" + Environment.NewLine + "_Not recorded yet._" + Environment.NewLine + Environment.NewLine +
        "## Open Questions" + Environment.NewLine + "_None recorded._" + Environment.NewLine;

    private static string? FindFirstHeading(string markdown)
    {
        foreach (string line in markdown.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith('#'))
                return trimmed.TrimStart('#').Trim();
        }

        return null;
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string CreateTaskId() => "TASK-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    private static bool IsSafeSegment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 80 || value is "." or "..")
            return false;

        foreach (char character in value)
        {
            if (!(char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
                return false;
        }

        return true;
    }

    private static void ValidateId(string value, string parameterName)
    {
        if (!IsSafeSegment(value))
            throw new ArgumentException("ID должен содержать только латинские буквы, цифры, дефис или подчёркивание.", parameterName);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

internal sealed class MarkdownFrontmatterBuilder
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<string>> _lists = new(StringComparer.OrdinalIgnoreCase);

    public void Add(string key, string value) => _values.Add(key, value);
    public void AddList(string key, IReadOnlyList<string> values) => _lists.Add(key, values);

    public string ToMarkdown(string body)
    {
        var builder = new StringBuilder(body.Length + 256);
        builder.AppendLine("---");
        foreach ((string key, string value) in _values.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            builder.Append(key).Append(": ").Append(Quote(value)).AppendLine();

        foreach ((string key, IReadOnlyList<string> values) in _lists.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(key).AppendLine(":");
            foreach (string value in values)
                builder.Append("  - ").Append(Quote(value)).AppendLine();
        }

        builder.AppendLine("---").AppendLine().Append(body.TrimStart('\r', '\n'));
        return builder.ToString();
    }

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal) + "\"";
}
