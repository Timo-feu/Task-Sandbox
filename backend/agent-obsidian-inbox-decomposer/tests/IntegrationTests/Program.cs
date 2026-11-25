using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using TaskSandbox;
using TaskSandbox.Execution;
using TaskSandbox.Inbox;
using TaskSandbox.Obsidian;
using TaskSandbox.Planning;
using TaskSandbox.Prompting;

internal static class Program
{
    private static async Task<int> Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "LocalCodingAgent.IntegrationTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await TestInboxDraftGateAsync(root);
            await TestFullPipelineAsync(root);
            TestRecovery(root);
            await TestGitAsync(root);
            Console.WriteLine("PASS: все интеграционные сценарии пройдены.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL: " + exception);
            return 1;
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task TestInboxDraftGateAsync(string root)
    {
        string vault = Path.Combine(root, "inbox-gate-vault");
        var store = new ObsidianTaskStore(new ObsidianTaskStoreOptions { VaultPath = vault });
        store.EnsureInitialized();
        string inbox = Path.Combine(store.AgentRootPath, "Inbox");

        await File.WriteAllTextAsync(Path.Combine(inbox, "draft.md"),
            "---\nid: draft-001\nstatus: draft\n---\n# Черновик\nТекст в процессе редактирования.");
        await File.WriteAllTextAsync(Path.Combine(inbox, "legacy.md"),
            "---\nid: legacy-001\n---\n# Старая заметка без статуса\nНе должна забираться автоматически.");
        await File.WriteAllTextAsync(Path.Combine(inbox, "empty-ready.md"),
            "---\nid: empty-001\nstatus: ready\n---\n");
        await File.WriteAllTextAsync(Path.Combine(inbox, "ready.md"),
            "---\nid: ready-001\nstatus: ready\n---\n# Опубликованная задача\nСодержимое задачи.");

        IReadOnlyList<ObsidianTaskDocument> ready = await store.ReadInboxAsync();
        Assert(ready.Count == 1, "Inbox должен возвращать только непустые заметки со status: ready.");
        Assert(ready[0].Id == "ready-001", "В обработку должна попасть только опубликованная заметка.");
        Assert(ready[0].Title == "Опубликованная задача", "Заголовок должен извлекаться из первого Markdown heading.");

        string firstDuplicate = Path.Combine(inbox, "duplicate-a.md");
        string secondDuplicate = Path.Combine(inbox, "duplicate-b.md");
        const string duplicateNote = "---\nid: duplicate-001\nstatus: ready\n---\n# Дубликат\nТекст задачи.";
        await File.WriteAllTextAsync(firstDuplicate, duplicateNote);
        await File.WriteAllTextAsync(secondDuplicate, duplicateNote);
        await store.MoveInboxTaskToPlanningAsync(firstDuplicate);
        bool duplicateRejected = false;
        try
        {
            await store.MoveInboxTaskToPlanningAsync(secondDuplicate);
        }
        catch (IOException)
        {
            duplicateRejected = true;
        }
        Assert(duplicateRejected, "Повторный родительский ID должен блокироваться.");
        Assert(File.Exists(secondDuplicate), "Дубликат не должен исчезать из Inbox незаметно.");

        Console.WriteLine("PASS: Inbox draft gate и защита от повторного родительского ID.");
    }

    private static async Task TestFullPipelineAsync(string root)
    {
        string vault = Path.Combine(root, "vault");
        string workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspace);

        var store = new ObsidianTaskStore(new ObsidianTaskStoreOptions { VaultPath = vault });
        store.EnsureInitialized();
        string inboxPath = Path.Combine(store.AgentRootPath, "Inbox", "parent-001.md");
        await File.WriteAllTextAsync(inboxPath,
            "---\nid: parent-001\nworkspace: demo\npriority: normal\ndependency_policy: existing_only\nstatus: ready\n---\n" +
            "# Integration smoke task\n\nCreate a harmless documentation-only task.\n");

        var provider = new ScriptedModelProvider();
        var prompts = new PromptSet("", "system", "planning", "execution", "memory", PromptFeatures.Memory);
        var contextBuilder = new TaskContextBuilder(store, prompts);
        var decomposer = new TaskDecomposer(provider, store, contextBuilder);
        var inbox = new InboxProcessor(store, decomposer, new InboxProcessorOptions
        {
            WorkspaceId = "demo",
            WorkspacePath = workspace,
            PollInterval = TimeSpan.FromSeconds(1)
        });
        var executor = new TaskExecutor(provider, contextBuilder, "demo", workspace,
            processTimeout: TimeSpan.FromSeconds(10), maxProcessOutputCharacters: 4096);
        var validator = new ResultValidator(workspace);
        var recovery = new WorkspaceRecoveryStore(workspace);
        var pipeline = new SelfCorrectionEngine(executor, validator, 0, recovery);
        var memoryUpdater = new ProjectMemoryUpdater(provider, store, "integration test memory prompt");
        var worker = new QueueWorker(store, pipeline, new QueueWorkerOptions
        {
            PollInterval = TimeSpan.FromSeconds(1)
        }, memoryUpdater);
        var runtime = new AgentRuntime(store, inbox, worker, TimeSpan.FromSeconds(1));

        await runtime.RunOnceAsync();

        string completed = Path.Combine(store.AgentRootPath, "Completed", "parent-001-01.md");
        Assert(File.Exists(completed), "Подзадача должна попасть в Completed.");
        string completedMarkdown = await File.ReadAllTextAsync(completed);
        Assert(completedMarkdown.Contains("status: \"completed\"", StringComparison.Ordinal), "Статус задачи должен быть completed.");

        string memoryPath = store.GetProjectMemoryPath("demo");
        Assert(File.Exists(memoryPath), "Project Memory должна быть создана.");
        string memory = await File.ReadAllTextAsync(memoryPath);
        Assert(memory.Contains("<!-- completed-task: parent-001-01 -->", StringComparison.Ordinal),
            "Project Memory должна содержать маркер завершённой задачи.");

        string planPath = Path.Combine(store.AgentRootPath, "Planning", "parent-001", "Plan.md");
        Assert(File.Exists(planPath), "План должен сохраниться в Planning.");
        string reportPath = Path.Combine(store.AgentRootPath, "Reports", "parent-001", "Step-parent-001-01.md");
        Assert(File.Exists(reportPath), "Отчёт выполнения должен сохраниться.");
        Assert(provider.CallCount == 3, "Должны пройти planning, execution и memory model calls.");
        CleanupRecoveryArtifacts(workspace);
        Console.WriteLine("PASS: Inbox → Planning → Queue → Execution → Validation → Project Memory → Completed.");
    }

    private static void TestRecovery(string root)
    {
        string workspace = Path.Combine(root, "recovery-workspace");
        Directory.CreateDirectory(workspace);
        string existingPath = Path.Combine(workspace, "existing.txt");
        File.WriteAllText(existingPath, "original");

        var recoveryStore = new WorkspaceRecoveryStore(workspace);
        FileRecoverySession session = recoveryStore.BeginTask("recovery-test");
        session.CaptureBeforeWrite("existing.txt", existingPath);
        File.WriteAllText(existingPath, "modified");
        string newPath = Path.Combine(workspace, "created.txt");
        session.CaptureBeforeWrite("created.txt", newPath);
        File.WriteAllText(newPath, "new");
        session.Rollback();

        Assert(File.ReadAllText(existingPath) == "original", "Recovery должен восстановить исходный файл.");
        Assert(!File.Exists(newPath), "Recovery должен удалить новый файл.");

        FileRecoverySession interrupted = recoveryStore.BeginTask("crash-test");
        interrupted.CaptureBeforeWrite("existing.txt", existingPath);
        File.WriteAllText(existingPath, "crashed-write");
        Assert(recoveryStore.RecoverInterruptedSessions() >= 1, "Recovery должен найти незавершённую сессию.");
        Assert(File.ReadAllText(existingPath) == "original", "Recovery после перезапуска должен восстановить файл.");
        CleanupRecoveryArtifacts(workspace);
        Console.WriteLine("PASS: rollback и восстановление прерванной recovery-сессии.");
    }

    private static async Task TestGitAsync(string root)
    {
        string workspace = Path.Combine(root, "git-workspace");
        Directory.CreateDirectory(workspace);
        await RunGitAsync(workspace, "init");
        await RunGitAsync(workspace, "config", "user.name", "Integration Test");
        await RunGitAsync(workspace, "config", "user.email", "integration@example.invalid");

        string file = Path.Combine(workspace, "README.md");
        await File.WriteAllTextAsync(file, "initial\n");
        await RunGitAsync(workspace, "add", "README.md");
        await RunGitAsync(workspace, "commit", "-m", "initial");
        await File.WriteAllTextAsync(file, "changed\n");

        var git = new GitService(workspace);
        GitCommandResult status = await git.GetStatusAsync();
        Assert(status.Success && status.Output.Contains("README.md", StringComparison.Ordinal),
            "git_status должен показывать изменённый файл.");
        GitCommandResult diff = await git.GetDiffAsync();
        Assert(diff.Success && diff.Output.Contains("changed", StringComparison.Ordinal),
            "git_diff должен содержать изменение.");
        GitCommandResult commit = await git.CommitAsync("integration commit", ["README.md"]);
        Assert(commit.Success, "Разрешённый локальный commit должен завершиться успешно: " + commit.Output);
        Console.WriteLine("PASS: Git status → diff → explicit local commit.");
    }

    private static void CleanupRecoveryArtifacts(string workspace)
    {
        string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
        string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData)) localData = Path.GetTempPath();
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        string recoveryPath = Path.Combine(localData, "LocalCodingAgent", "Recovery", key);
        if (Directory.Exists(recoveryPath)) Directory.Delete(recoveryPath, recursive: true);
    }

    private static async Task RunGitAsync(string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить git для подготовки теста.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        string output = (await stdout) + (await stderr);
        if (process.ExitCode != 0) throw new InvalidOperationException("Подготовка Git-теста завершилась ошибкой: " + output);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class ScriptedModelProvider : IModelProvider
    {
        private int _callCount;
        public int CallCount => _callCount;

        public Task StreamAsync(ModelRequest request, Func<string, ValueTask> onContent,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int call = Interlocked.Increment(ref _callCount);
            string response = call switch
            {
                1 => """{"title":"Integration plan","summary":"One documentation-only subtask.","subtasks":[{"key":"docs","title":"Complete smoke task","description":"Complete the integration smoke task without modifying source code.","depends_on":[],"target_files":[],"requirements":[],"acceptance_criteria":["Task reaches Completed"],"priority":"normal"}]}""",
                2 => """{"type":"final","success":true,"summary":"Smoke task completed.","checks":["No source files changed"]}""",
                3 => "# Project Memory\n\n## Desired Outcome\n- Integration test project.\n\n## Current State\n- Pipeline smoke test passed.\n\n## Completed\n- Completed smoke task.\n\n## Next Steps\n- Continue backend verification.\n\n## Constraints and Decisions\n- No external test packages.\n\n## Open Questions\n- None.\n",
                _ => throw new InvalidOperationException($"Неожиданный вызов модели #{call}.")
            };
            return onContent(response).AsTask();
        }
    }
}
