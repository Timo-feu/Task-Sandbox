using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.FileProviders;
using TaskSandbox.Configuration;
using TaskSandbox.Obsidian;

namespace TaskSandbox.Web;

/// <summary>Local-only HTTP API and static dashboard for TS — Task Sandbox.</summary>
public static class WebDashboardHost
{
    private static readonly string[] TaskFolders = ["Inbox", "Planning", "Queue", "InProgress", "Completed", "Failed"];

    private sealed record DashboardTask(string Id, string Title, string Description, string Status, string Priority, string WorkspaceId, string ParentId, DateTime UpdatedUtc);

    public static async Task RunAsync(AgentSettings settings, CancellationToken cancellationToken)
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
        app.MapGet("/api/health", () => Results.Ok(new { status = "online", product = "TS — Task Sandbox" }));
        app.MapGet("/api/dashboard", () => Results.Ok(BuildDashboard(settings, agentRoot)));
        app.MapGet("/api/tasks", () => Results.Ok(ReadTasks(agentRoot)));
        app.MapGet("/api/settings", () => Results.Ok(new
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
                workspace.Id, workspace.Path, isDefault = workspace.Id == settings.Workspaces.DefaultId
            }),
            obsidian = new { settings.Obsidian.VaultPath }
        }));

        Console.WriteLine("[Web] TS — Task Sandbox: http://127.0.0.1:5080");
        await app.RunAsync(cancellationToken).ConfigureAwait(false);
    }

    private static object BuildDashboard(AgentSettings settings, string root)
    {
        int Count(string folder) => CountMarkdown(Path.Combine(root, folder));
        DashboardTask? currentTask = ReadTasks(root).FirstOrDefault(task => task.Status.Equals("InProgress", StringComparison.OrdinalIgnoreCase));
        int completedToday = CountMarkdown(Path.Combine(root, "Completed"), DateTime.UtcNow.Date);
        ModelProfileSettings? active = settings.Model.Profiles.FirstOrDefault(profile =>
            profile.Id.Equals(settings.Model.ActiveProfileId, StringComparison.OrdinalIgnoreCase));

        return new
        {
            agent = new { status = "running" },
            currentTask,
            today = new { completed = completedToday },
            totals = new
            {
                inbox = Count("Inbox"), planning = Count("Planning"), queued = Count("Queue"),
                inProgress = Count("InProgress"), completed = Count("Completed"), failed = Count("Failed")
            },
            // These metrics require runtime event instrumentation; never fabricate them from file counts.
            activityPercent = (int?)null,
            errorsToday = (int?)null,
            fixesToday = (int?)null,
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
