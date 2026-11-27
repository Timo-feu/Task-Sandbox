using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.FileProviders;
using TaskSandbox.Configuration;
using TaskSandbox.Obsidian;
using TaskSandbox.Execution;

namespace TaskSandbox.Web;

/// <summary>Local-only HTTP API and static dashboard for TS — Task Sandbox.</summary>
public static class WebDashboardHost
{
    private static readonly string[] TaskFolders = ["Inbox", "Planning", "Queue", "InProgress", "Completed", "Failed"];
    private static readonly SemaphoreSlim TaskFileLock = new(1, 1);

    private sealed record DashboardTask(string Id, string Title, string Description, string Status, string Priority, string WorkspaceId, string ParentId, DateTime UpdatedUtc);
    public sealed record UpdateTaskRequest(string? Title, string? Description, string? Priority);

    public static async Task RunAsync(AgentSettings settings, RuntimeEventJournal journal, CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateBuilder(Array.Empty<string>());
        builder.WebHost.UseUrls("http://127.0.0.1:5080");
        
        await using WebApplication app = builder.Build();
        string webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (Directory.Exists(webRoot))
        {
            app.UseDefaultFiles(new DefaultFilesOptions
            {
                FileProvider = new PhysicalFileProvider(webRoot)
            });
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(webRoot)
            });
        }

        string agentRoot = Path.Combine(Path.GetFullPath(settings.Obsidian.VaultPath), "LocalCodingAgent");
        app.MapGet("/api/health", () => Results.Ok(new
        {
            status = "online",
            product = "TS — Task Sandbox",
            serverTimeUtc = DateTime.UtcNow,
            processId = Environment.ProcessId
        }));
        app.MapGet("/api/dashboard", () => Results.Ok(BuildDashboard(settings, agentRoot, journal)));
        app.MapGet("/api/tasks", () => Results.Ok(ReadTasks(agentRoot)));
        app.MapGet("/api/activity", (int? limit) => Results.Ok(journal.ReadRecent(limit ?? 100)));
        var taskStore = new ObsidianTaskStore(new ObsidianTaskStoreOptions
        {
            VaultPath = settings.Obsidian.VaultPath
        });
        app.MapPost("/api/tasks/{id}/retry", async (string id, CancellationToken token) =>
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 80)
                return Results.BadRequest(new { error = "Некорректный ID задачи." });

            await TaskFileLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await taskStore.RequeueFailedTaskAsync(id, token).ConfigureAwait(false);
                return Results.Ok(new { queued = true, id });
            }
            catch (FileNotFoundException)
            {
                return Results.NotFound(new { error = "Неудачная задача не найдена. Возможно, она уже была возвращена в очередь." });
            }
            catch (InvalidDataException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
            catch (IOException exception)
            {
                return Results.Conflict(new { error = exception.Message });
            }
            finally
            {
                TaskFileLock.Release();
            }
        });
        app.MapPut("/api/tasks/{id}", async (string id, UpdateTaskRequest request, CancellationToken token) =>
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 160 ||
                (request.Title?.Length ?? 0) > 240 || (request.Description?.Length ?? 0) > 100_000 ||
                (request.Priority is not null && request.Priority is not ("low" or "normal" or "high" or "urgent")))
                return Results.BadRequest(new { error = "Некорректные данные задачи." });

            string? file = FindTaskFile(agentRoot, id);
            if (file is null) return Results.NotFound(new { error = "Задача не найдена." });

            await TaskFileLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                string markdown = await File.ReadAllTextAsync(file, token).ConfigureAwait(false);
                MarkdownFrontmatter frontmatter = MarkdownFrontmatter.Parse(markdown);
                if (request.Title is not null) frontmatter.SetValue("title", request.Title.Trim());
                if (request.Priority is not null) frontmatter.SetValue("priority", request.Priority);
                if (request.Description is not null) frontmatter.Body = request.Description.Trim();
                string temporary = file + ".ts-tmp";
                await File.WriteAllTextAsync(temporary, frontmatter.ToMarkdown(), token).ConfigureAwait(false);
                File.Move(temporary, file, overwrite: true);
                return Results.Ok(new { saved = true });
            }
            catch (InvalidDataException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
            finally
            {
                TaskFileLock.Release();
            }
        });
        app.MapGet("/api/settings", () =>
        {
            IReadOnlyList<DashboardTask> tasks = ReadTasks(agentRoot);
            return Results.Ok(new
            {
                productName = "TS — Task Sandbox",
                activeProfileId = settings.Model.ActiveProfileId,
                profiles = settings.Model.Profiles.Select(profile => new
                {
                    profile.Id, profile.Name, profile.ProviderId, profile.BaseUrl, profile.ModelId,
                    profile.RequestTimeoutSeconds
                }),
                workspaces = settings.Workspaces.Registered.Select(workspace => new
                {
                    workspace.Id, workspace.Path, isDefault = workspace.Id == settings.Workspaces.DefaultId,
                    exists = Directory.Exists(workspace.Path),
                    taskCount = tasks.Count(task => string.Equals(task.WorkspaceId, workspace.Id, StringComparison.OrdinalIgnoreCase))
                }),
                obsidian = new { settings.Obsidian.VaultPath }
            });
        });

        Console.WriteLine("[Web] TS — Task Sandbox: http://127.0.0.1:5080");
        await app.RunAsync(cancellationToken).ConfigureAwait(false);
    }

    private static object BuildDashboard(AgentSettings settings, string root, RuntimeEventJournal journal)
    {
        int Count(string folder) => CountMarkdown(Path.Combine(root, folder));
        DashboardTask? currentTask = ReadTasks(root).FirstOrDefault(task => task.Status.Equals("InProgress", StringComparison.OrdinalIgnoreCase));
        int completedToday = CountMarkdown(Path.Combine(root, "Completed"), DateTime.UtcNow.Date);
        ModelProfileSettings? active = settings.Model.Profiles.FirstOrDefault(profile =>
            profile.Id.Equals(settings.Model.ActiveProfileId, StringComparison.OrdinalIgnoreCase));
        (int eventsToday, int errors, int fixes) = journal.GetTodaySummary();
        RuntimeEvent? currentActivity = currentTask is null
            ? null
            : journal.ReadRecent(200).FirstOrDefault(item => item.TaskId == currentTask.Id);

        return new
        {
            agent = new { status = "running" },
            currentTask,
            currentActivity,
            today = new { completed = completedToday },
            totals = new
            {
                inbox = Count("Inbox"), planning = Count("Planning"), queued = Count("Queue"),
                inProgress = Count("InProgress"), completed = Count("Completed"), failed = Count("Failed")
            },
            eventsToday,
            errorsToday = errors,
            fixesToday = fixes,
            model = active is null ? null : new { providerId = active.ProviderId, providerName = ProviderCatalogName(active.ProviderId), active.ModelId }
        };
    }

    private static string ProviderCatalogName(string providerId) =>
        TaskSandbox.Models.ProviderCatalog.TryGet(providerId, out var provider) ? provider?.Name ?? providerId : providerId;

    private static int CountMarkdown(string directory, DateTime? modifiedSinceUtc = null)
    {
        if (!Directory.Exists(directory)) return 0;
        int count = 0;
        foreach (string file in EnumerateMarkdownSafe(directory))
        {
            var info = new FileInfo(file);
            if (modifiedSinceUtc is null || info.LastWriteTimeUtc >= modifiedSinceUtc.Value) count++;
        }
        return count;
    }

    private static IEnumerable<string> EnumerateMarkdownSafe(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            var directoryInfo = new DirectoryInfo(directory);
            if ((directoryInfo.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            foreach (string file in Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0)
                    yield return file;
            }
            foreach (string child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly))
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                    pending.Push(child);
            }
        }
    }


    private static string? FindTaskFile(string root, string id)
    {
        foreach (string folder in TaskFolders)
        {
            string directory = Path.Combine(root, folder);
            if (!Directory.Exists(directory)) continue;
            foreach (string file in EnumerateMarkdownSafe(directory))
            {
                var info = new FileInfo(file);
                if (info.Length > 1_048_576) continue;
                try
                {
                    MarkdownFrontmatter frontmatter = MarkdownFrontmatter.Parse(File.ReadAllText(file));
                    string taskId = frontmatter.GetValue("id", Path.GetFileNameWithoutExtension(file));
                    if (string.Equals(taskId, id, StringComparison.Ordinal)) return file;
                }
                catch (InvalidDataException) { }
            }
        }
        return null;
    }

    private static IReadOnlyList<DashboardTask> ReadTasks(string root)
    {
        var result = new List<DashboardTask>();
        foreach (string folder in TaskFolders)
        {
            string directory = Path.Combine(root, folder);
            if (!Directory.Exists(directory)) continue;
            foreach (string file in EnumerateMarkdownSafe(directory))
            {
                var info = new FileInfo(file);
                if (info.Length > 1_048_576) continue;
                try
                {
                    string markdown = File.ReadAllText(file);
                    MarkdownFrontmatter frontmatter = MarkdownFrontmatter.Parse(markdown);
                    string id = frontmatter.GetValue("id", Path.GetFileNameWithoutExtension(file));
                    string title = frontmatter.GetValue("title", id);
                    result.Add(new DashboardTask(
                        id, title, frontmatter.Body.Trim(), folder,
                        frontmatter.GetValue("priority", "normal"),
                        frontmatter.GetValue("workspace"),
                        frontmatter.GetValue("parent_id"),
                        info.LastWriteTimeUtc));
                }
                catch (InvalidDataException) { }
            }
        }
        return result.OrderByDescending(item => item.UpdatedUtc).ToArray();
    }
}
