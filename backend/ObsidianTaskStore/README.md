# Prompt System + Project Memory + Obsidian Task Store

Target framework: `net10.0`  
External NuGet packages: none.

## Included

- External Markdown prompt templates and prompt loader.
- Project memory stored outside the code Workspace.
- `ObsidianTaskStore`: file-system integration with a user-selected Obsidian vault.
- Markdown Inbox import, Planning storage, atomic task-plan publication, dependency-aware task claiming, Completed/Failed transitions, reports, and project memory notes inside the vault.
- No Obsidian API, plugin, database, YAML package, or NuGet dependency.

## Configure the vault

Set an environment variable to the path of the existing Obsidian vault:

```powershell
$env:LOCAL_CODING_AGENT_OBSIDIAN_VAULT = "D:\Notes\MyVault"
```

Do not point this setting at the Workspace repository. The agent manages only the `LocalCodingAgent` folder inside the selected vault.

## Vault structure

```text
MyVault/
└── LocalCodingAgent/
    ├── Inbox/
    ├── Planning/
    │   └── TASK-.../
    │       ├── Original.md
    │       └── Plan.md
    ├── Queue/
    │   └── TASK-.../
    │       ├── TASK-...-01.md
    │       └── TASK-...-02.md
    ├── InProgress/
    ├── Completed/
    ├── Failed/
    ├── Projects/
    │   └── my-project/
    │       └── ProjectMemory.md
    └── Reports/
        └── TASK-.../
            ├── Step-TASK-...-01.md
            └── Failure-TASK-...-02.md
```

The store creates these directories without replacing existing notes.

## Inbox note format

Create a Markdown file in `LocalCodingAgent/Inbox`. `workspace` is a configured workspace identifier, not a filesystem path. Use a simple stable ID such as `my-project`.

```markdown
---
workspace: my-project
priority: high
---

# Add account login

## Goal
Implement login for existing users.

## Requirements
- Validate credentials.
- Keep current project conventions.

## Acceptance criteria
- Valid credentials authenticate.
- Invalid credentials are rejected.
```

`id` is optional. If omitted, the store generates an ID when the note is moved to Planning. Frontmatter supports scalar values, inline lists, and indented list entries. It is intentionally a small task-metadata subset, not a general YAML parser.

## Minimal usage

```csharp
using TaskSandbox.Obsidian;

string vaultPath = Environment.GetEnvironmentVariable(
    "LOCAL_CODING_AGENT_OBSIDIAN_VAULT")
    ?? throw new InvalidOperationException("Obsidian vault path is not configured.");

var store = new ObsidianTaskStore(new ObsidianTaskStoreOptions
{
    VaultPath = vaultPath,
    AgentFolderName = "LocalCodingAgent"
});

store.EnsureInitialized();

// 1. Read Inbox and move a selected note into Planning.
IReadOnlyList<ObsidianTaskDocument> inbox = await store.ReadInboxAsync(cancellationToken);
if (inbox.Count > 0)
{
    ObsidianTaskDocument original = await store.MoveInboxTaskToPlanningAsync(
        inbox[0].SourcePath, cancellationToken);

    // 2. Later, after the Decomposer returns a validated plan:
    var plan = new ObsidianTaskPlan(
        ParentTaskId: original.Id,
        Title: original.Title,
        PlanMarkdown: "# Plan\n\nDecomposition summary.",
        Subtasks:
        [
            new ObsidianSubtaskDefinition(
                Id: original.Id + "-01",
                Title: "Analyze current architecture",
                Description: "Inspect existing components and report integration points.",
                WorkspaceId: original.WorkspaceId ?? "my-project",
                DependsOn: [],
                TargetFiles: [],
                Requirements: ["Do not modify source files during analysis."],
                AcceptanceCriteria: ["Integration points are documented."])
        ]);

    await store.PublishPlanAsync(plan, cancellationToken);
}

// 3. The Executor claims only a task whose dependencies are completed.
int recovered = await store.RecoverInProgressTasksAsync(cancellationToken);
Console.WriteLine($"Recovered after restart: {recovered}");

ObsidianTaskDocument? task = await store.ClaimNextReadyTaskAsync(cancellationToken);
if (task is not null)
{
    // Execute task.Body with TaskExecutionPrompt and current project memory.
    await store.CompleteTaskAsync(
        task.Id,
        "# Result\n\nThe subtask was completed and validated.",
        cancellationToken);
}
```

## Lifecycle

- `Inbox` -> `Planning`: `MoveInboxTaskToPlanningAsync`.
- `Planning` -> `Queue`: `PublishPlanAsync` publishes all subtasks by moving one staging directory into Queue. Readers do not see an incomplete task batch.
- `Queue` -> `InProgress`: `ClaimNextReadyTaskAsync` claims the highest-priority task whose dependencies all exist in Completed.
- `InProgress` -> `Completed`: `CompleteTaskAsync` writes a report and moves the task.
- `InProgress` -> `Failed`: `FailTaskAsync` writes a failure report and moves the task.
- `Failed` -> `Queue`: `RequeueFailedTaskAsync`.
- `InProgress` -> `Queue` after application restart: `RecoverInProgressTasksAsync`.
- `attempt` is incremented each time a task is claimed.

Dependencies are task IDs in the same plan. The plan is rejected if it has duplicate IDs, unknown dependencies, self-dependencies, or dependency cycles. Initial execution is deliberately single-consumer; the API does not yet implement a multi-process lease/lock protocol.

## Current integration boundary

This step adds the Obsidian storage module and its APIs, but does not yet replace the previous console demo flow in `Program.cs`. The current `Program` still reads `prompts.txt` and uses the earlier `%LOCALAPPDATA%\LocalCodingAgent\WorkspaceMemory` `ProjectMemoryStore`. The new Obsidian-backed canonical memory path is `LocalCodingAgent/Projects/<workspace-id>/ProjectMemory.md`; the next integration step will switch the runtime to that source so there is only one active project-memory store.

## Safety and limitations

- Model prompts never receive arbitrary Vault filesystem access. The application calls this store with IDs and managed paths.
- IDs and workspace aliases are restricted to ASCII letters, digits, `_`, and `-` to prevent path traversal.
- The store checks managed paths for Windows reparse points/symbolic links before accessing them.
- Files are written to a temporary sibling and renamed into place; a whole decomposer batch is published by one directory move.
- Markdown files are capped at 1 MiB by default. Adjust `MaxMarkdownFileBytes` if needed.
- The task Frontmatter parser supports the subset used by this application. It is not intended to parse arbitrary YAML features, Obsidian embeds, or nested YAML objects.
- `FileSystemWatcher` is intentionally not included yet. The next orchestration layer can poll Inbox/Queue on a bounded interval and always reconcile the actual filesystem state after startup.
- This step does not yet include the Workspace scanner, Task Decomposer prompt, Agent Execution Loop, or automatic Project Memory summarization.

## Build

```powershell
dotnet clean .\backend.csproj
dotnet build .\backend.csproj -c Release
```

No project packages are required. All new `.cs` files are included by the SDK's default compile glob.
