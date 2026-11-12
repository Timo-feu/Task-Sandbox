using TaskSandbox.Configuration;
using TaskSandbox.Execution;
using TaskSandbox.Inbox;
using TaskSandbox.Obsidian;
using TaskSandbox.Planning;
using TaskSandbox.Prompting;

namespace TaskSandbox;

public static class Program
{
    public static async Task Main(string[] args)
    {
        using var cancellationSource = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellationSource.Cancel();
        };

        CancellationToken cancellationToken = cancellationSource.Token;

        try
        {
            ConfigLoadResult configResult = await ConfigManager.LoadAsync(cancellationToken)
                .ConfigureAwait(false);
            AgentSettings settings = configResult.Settings;
            ReportConfigurationLoad(configResult);

            PromptSet prompts = await PromptLoader.LoadAsync(
                settings.Prompts.DirectoryPath,
                cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"[Prompts] Markdown-промпты загружены: {prompts.DirectoryPath}");
            Console.WriteLine($"[Prompts] TaskPlanningPrompt: {prompts.TaskPlanningPrompt.Length:N0} символов");

            if (!TryGetConfiguredWorkspace(settings, out WorkspaceEntry? workspace) || workspace is null)
            {
                Console.WriteLine(
                    $"[Setup] Настройте Obsidian.VaultPath и Workspaces.Registered в файле: {configResult.SettingsPath}");
                return;
            }

            string vaultPath = settings.Obsidian.VaultPath;
            string workspacePath = workspace.Path;
            string workspaceId = workspace.Id;

            if (!Directory.Exists(vaultPath))
            {
                Console.WriteLine($"[Setup] Obsidian Vault не найден: {vaultPath}");
                Console.WriteLine($"[Setup] Исправьте Obsidian.VaultPath в {configResult.SettingsPath}");
                return;
            }

            if (!Directory.Exists(workspacePath))
            {
                Console.WriteLine($"[Setup] Workspace не найден: {workspacePath}");
                Console.WriteLine($"[Setup] Исправьте путь Workspace '{workspaceId}' в {configResult.SettingsPath}");
                return;
            }

            if (PathsOverlap(vaultPath, workspacePath))
            {
                Console.Error.WriteLine(
                    "[Setup] Obsidian Vault и Workspace должны находиться в разных, непересекающихся директориях.");
                Environment.ExitCode = 1;
                return;
            }

            using var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(15),
                UseProxy = false
            };

            using var httpClient = new HttpClient(handler)
            {
                BaseAddress = NormalizeBaseAddress(settings.Model.BaseUrl),
                Timeout = Timeout.InfiniteTimeSpan
            };

            TimeSpan requestTimeout = TimeSpan.FromSeconds(settings.Model.RequestTimeoutSeconds);
            IModelProvider modelProvider = await CreateModelProviderAsync(
                httpClient,
                settings.Model.ModelId,
                requestTimeout,
                cancellationToken).ConfigureAwait(false);

            var taskStore = new ObsidianTaskStore(new ObsidianTaskStoreOptions
            {
                VaultPath = vaultPath
            });
            taskStore.EnsureInitialized();

            var taskContextBuilder = new TaskContextBuilder(taskStore, prompts);
            var decomposer = new TaskDecomposer(modelProvider, taskStore, taskContextBuilder);
            var inboxProcessor = new InboxProcessor(
                taskStore,
                decomposer,
                new InboxProcessorOptions
                {
                    WorkspaceId = workspaceId,
                    WorkspacePath = workspacePath,
                    PollInterval = TimeSpan.FromSeconds(settings.Runtime.InboxPollIntervalSeconds)
                });

            if (settings.Runtime.MaxConcurrentTasks > 1)
                Console.WriteLine("[Runtime] Queue Worker пока работает последовательно; runtime.maxConcurrentTasks временно не применяется.");

            var executor = new TaskExecutor(
                modelProvider, taskContextBuilder, workspaceId, workspacePath,
                TimeSpan.FromSeconds(settings.Runtime.ProcessTimeoutSeconds),
                settings.Runtime.MaxProcessOutputCharacters);
            var queueWorker = new QueueWorker(taskStore, executor, new QueueWorkerOptions
            {
                PollInterval = TimeSpan.FromSeconds(settings.Runtime.InboxPollIntervalSeconds)
            });
            var runtime = new AgentRuntime(
                taskStore,
                inboxProcessor,
                queueWorker,
                TimeSpan.FromSeconds(settings.Runtime.InboxPollIntervalSeconds));

            if (args.Contains("--once", StringComparer.OrdinalIgnoreCase))
            {
                await runtime.RunOnceAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            await runtime.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine("[System] Получена команда остановки.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Console.Error.WriteLine($"[Fatal] {exception.Message}");
            Environment.ExitCode = 1;
        }
    }

    private static void ReportConfigurationLoad(ConfigLoadResult result)
    {
        if (result.WasCreated)
        {
            Console.WriteLine($"[Config] Создан файл настроек: {result.SettingsPath}");
            Console.WriteLine("[Config] Укажите Obsidian Vault и хотя бы один Workspace в settings.json.");
            return;
        }

        if (result.WasReset)
        {
            Console.Error.WriteLine("[Config] ВНИМАНИЕ: настройки повреждены или несовместимы. Создан новый settings.json со значениями по умолчанию.");
            Console.Error.WriteLine($"[Config] Причина: {result.ResetReason}");
            Console.Error.WriteLine($"[Config] Предыдущий файл сохранён: {result.BackupPath}");
            return;
        }

        Console.WriteLine($"[Config] Настройки загружены: {result.SettingsPath}");
    }

    private static bool TryGetConfiguredWorkspace(AgentSettings settings, out WorkspaceEntry? workspace)
    {
        workspace = null;
        if (string.IsNullOrWhiteSpace(settings.Obsidian.VaultPath) ||
            settings.Workspaces.Registered.Count == 0)
        {
            return false;
        }

        foreach (WorkspaceEntry candidate in settings.Workspaces.Registered)
        {
            if (!string.Equals(candidate.Id, settings.Workspaces.DefaultId, StringComparison.OrdinalIgnoreCase))
                continue;

            workspace = candidate;
            return true;
        }

        return false;
    }

    private static async Task<IModelProvider> CreateModelProviderAsync(
        HttpClient httpClient,
        string preferredModel,
        TimeSpan requestTimeout,
        CancellationToken cancellationToken)
    {
        var discoveryProvider = new OpenAiCompatibleProvider(httpClient, preferredModel, requestTimeout);

        ModelDiscoveryResult discovery;
        try
        {
            discovery = await discoveryProvider.DiscoverModelsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TimeoutException or
                                          System.Text.Json.JsonException or InvalidDataException)
        {
            Console.WriteLine(
                $"[Models] Discovery недоступен ({exception.Message}). Используется настроенная модель: {preferredModel}");
            return discoveryProvider;
        }

        if (!discovery.IsSupported || discovery.Models.Count == 0)
        {
            Console.WriteLine("[Models] Список моделей недоступен. Используется настроенная модель: " + preferredModel);
            return discoveryProvider;
        }

        ModelInfo selectedModel = discovery.Models[0];
        foreach (ModelInfo model in discovery.Models)
        {
            Console.WriteLine(
                $"[Models] {model.Id}{(string.IsNullOrWhiteSpace(model.OwnedBy) ? string.Empty : $" ({model.OwnedBy})")}");
            if (string.Equals(model.Id, preferredModel, StringComparison.Ordinal))
                selectedModel = model;
        }

        Console.WriteLine($"[Models] Выбрана модель: {selectedModel.Id}");
        return new OpenAiCompatibleProvider(httpClient, selectedModel.Id, requestTimeout);
    }

    private static bool PathsOverlap(string left, string right)
    {
        string fullLeft = Path.TrimEndingDirectorySeparator(Path.GetFullPath(left));
        string fullRight = Path.TrimEndingDirectorySeparator(Path.GetFullPath(right));
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return IsSameOrChild(fullLeft, fullRight, comparison) ||
               IsSameOrChild(fullRight, fullLeft, comparison);
    }

    private static bool IsSameOrChild(string possibleChild, string parent, StringComparison comparison)
    {
        if (string.Equals(possibleChild, parent, comparison))
            return true;

        string prefix = Path.EndsInDirectorySeparator(parent)
            ? parent
            : parent + Path.DirectorySeparatorChar;
        return possibleChild.StartsWith(prefix, comparison);
    }

    private static Uri NormalizeBaseAddress(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || uri is null ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                $"Некорректный адрес Model API: '{value}'. Требуется абсолютный HTTP/HTTPS URL.");
        }

        string normalized = uri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? uri.AbsoluteUri
            : uri.AbsoluteUri + "/";
        return new Uri(normalized, UriKind.Absolute);
    }
}
