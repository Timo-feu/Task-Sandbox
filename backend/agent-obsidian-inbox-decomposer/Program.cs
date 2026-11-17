using TaskSandbox.Configuration;
using TaskSandbox.Execution;
using TaskSandbox.Inbox;
using TaskSandbox.Obsidian;
using TaskSandbox.Planning;
using TaskSandbox.Prompting;
using TaskSandbox.Models;
using TaskSandbox.Web;

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

            if (await HandleModelProfileCommandAsync(args, settings, cancellationToken).ConfigureAwait(false))
                return;

            if (!TryGetConfiguredWorkspace(settings, out WorkspaceEntry? workspace) || workspace is null)
            {
                Console.WriteLine(
                    $"[Setup] Настройте Obsidian.VaultPath и Workspaces.Registered в файле: {configResult.SettingsPath}");
                return;
            }

            string vaultPath = settings.Obsidian.VaultPath;
            string workspacePath = workspace.Path;
            string workspaceId = workspace.Id;

            if (!Directory.Exists(workspacePath))
            {
                Console.WriteLine($"[Setup] Workspace не найден: {workspacePath}");
                Console.WriteLine($"[Setup] Исправьте путь Workspace '{workspaceId}' в {configResult.SettingsPath}");
                return;
            }

            if (args.Contains("--git-status", StringComparer.OrdinalIgnoreCase) ||
                args.Contains("--git-diff", StringComparer.OrdinalIgnoreCase) ||
                args.Contains("--git-commit", StringComparer.OrdinalIgnoreCase))
            {
                var git = new GitService(workspacePath);
                GitCommandResult gitResult;
                if (args.Contains("--git-status", StringComparer.OrdinalIgnoreCase))
                {
                    gitResult = await git.GetStatusAsync(cancellationToken).ConfigureAwait(false);
                }
                else if (args.Contains("--git-diff", StringComparer.OrdinalIgnoreCase))
                {
                    gitResult = await git.GetDiffAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    string? message = GetOptionValue(args, "--message");
                    string[] paths = GetOptionValues(args, "--path");
                    if (string.IsNullOrWhiteSpace(message) || paths.Length == 0)
                    {
                        Console.Error.WriteLine("Использование коммита: --git-commit --message \"сообщение\" --path relative/file [--path another/file]");
                        Environment.ExitCode = 2;
                        return;
                    }
                    gitResult = await git.CommitAsync(message, paths, cancellationToken).ConfigureAwait(false);
                }

                Console.WriteLine(gitResult.Output);
                if (!gitResult.Success) Environment.ExitCode = 1;
                return;
            }

            PromptSet prompts = await PromptLoader.LoadAsync(
                settings.Prompts.DirectoryPath,
                cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"[Prompts] Markdown-промпты загружены: {prompts.DirectoryPath}");
            Console.WriteLine($"[Prompts] TaskPlanningPrompt: {prompts.TaskPlanningPrompt.Length:N0} символов");

            if (!Directory.Exists(vaultPath))
            {
                Console.WriteLine($"[Setup] Obsidian Vault не найден: {vaultPath}");
                Console.WriteLine($"[Setup] Исправьте Obsidian.VaultPath в {configResult.SettingsPath}");
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

            var modelProviderFactory = new ModelProviderFactory();
            ProviderDefinition providerDefinition = ProviderCatalog.GetRequired(settings.Model.ProviderId);
            Console.WriteLine($"[Model] Профиль: {settings.Model.ActiveProfileId}");
            Console.WriteLine($"[Model] Провайдер: {providerDefinition.Name}");
            Console.WriteLine($"[Model] Endpoint: {settings.Model.BaseUrl}");

            var secretStore = new WindowsCredentialSecretStore();
            string? apiKey = secretStore.Get(
                ModelProfileService.GetCredentialTarget(settings.Model.ActiveProfileId));
            if (providerDefinition.RequiresApiKey && string.IsNullOrWhiteSpace(apiKey))
            {
                Console.Error.WriteLine(
                    $"[Model] Для профиля '{settings.Model.ActiveProfileId}' не задан API-ключ. " +
                    "Сохраните его командой --model-key-set.");
                Environment.ExitCode = 2;
                return;
            }
            if (!string.IsNullOrWhiteSpace(apiKey))
                Console.WriteLine("[Model] API-ключ: установлен в Windows Credential Manager.");

            IModelProvider discoveryProvider = modelProviderFactory.Create(settings.Model, httpClient, apiKey);
            string selectedModelId = await DiscoverConfiguredModelsAsync(
                discoveryProvider, settings.Model.ModelId, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(selectedModelId, settings.Model.ModelId, StringComparison.Ordinal))
                settings.Model.ModelId = selectedModelId;

            IModelProvider modelProvider = modelProviderFactory.Create(settings.Model, httpClient, apiKey);

            var taskStore = new ObsidianTaskStore(new ObsidianTaskStoreOptions
            {
                VaultPath = vaultPath
            });
            taskStore.EnsureInitialized();

            var journal = new RuntimeEventJournal(Path.Combine(vaultPath, "LocalCodingAgent", "Logs"));
            await journal.WriteAsync("agent_started", "Агент запущен", $"Workspace: {workspaceId}", cancellationToken: CancellationToken.None).ConfigureAwait(false);
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
                }, journal);

            if (settings.Runtime.MaxConcurrentTasks > 1)
                Console.WriteLine("[Runtime] Queue Worker пока работает последовательно; runtime.maxConcurrentTasks временно не применяется.");

            var executor = new TaskExecutor(
                modelProvider, taskContextBuilder, workspaceId, workspacePath,
                TimeSpan.FromSeconds(settings.Runtime.ProcessTimeoutSeconds),
                settings.Runtime.MaxProcessOutputCharacters, journal);
            var validator = new ResultValidator(workspacePath);
            var recoveryStore = new WorkspaceRecoveryStore(workspacePath);
            int recoveredFiles = recoveryStore.RecoverInterruptedSessions();
            if (recoveredFiles > 0)
                Console.WriteLine($"[Recovery] Восстановлено прерванных файловых транзакций: {recoveredFiles}.");
            var executionPipeline = new SelfCorrectionEngine(
                executor, validator, settings.Runtime.MaxCorrectionAttempts, recoveryStore, journal);
            ProjectMemoryUpdater? memoryUpdater = (prompts.Features & PromptFeatures.Memory) != 0
                ? new ProjectMemoryUpdater(modelProvider, taskStore, prompts.ProjectMemoryUpdatePrompt)
                : null;
            var queueWorker = new QueueWorker(taskStore, executionPipeline, new QueueWorkerOptions
            {
                PollInterval = TimeSpan.FromSeconds(settings.Runtime.InboxPollIntervalSeconds)
            }, memoryUpdater, journal);
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

            Task runtimeTask = runtime.RunAsync(cancellationToken);
            Task webTask = WebDashboardHost.RunAsync(settings, journal, cancellationToken);
            await Task.WhenAll(runtimeTask, webTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine("[System] Получена команда остановки.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidOperationException or System.ComponentModel.Win32Exception or
                                          NotSupportedException or ArgumentException or KeyNotFoundException or
                                          InvalidDataException)
        {
            Console.Error.WriteLine($"[Fatal] {exception.Message}");
            Environment.ExitCode = 1;
        }
    }

    private static async Task<bool> HandleModelProfileCommandAsync(
        string[] args,
        AgentSettings settings,
        CancellationToken cancellationToken)
    {
        bool listProfiles = args.Contains("--model-profiles", StringComparer.OrdinalIgnoreCase);
        bool addProfile = args.Contains("--model-profile-add", StringComparer.OrdinalIgnoreCase);
        bool useProfile = args.Contains("--model-use", StringComparer.OrdinalIgnoreCase);
        bool setKey = args.Contains("--model-key-set", StringComparer.OrdinalIgnoreCase);
        bool deleteKey = args.Contains("--model-key-delete", StringComparer.OrdinalIgnoreCase);
        if (!listProfiles && !addProfile && !useProfile && !setKey && !deleteKey)
            return false;

        var secretStore = new WindowsCredentialSecretStore();
        var profileService = new ModelProfileService(settings, secretStore);

        if (listProfiles)
        {
            foreach (ModelProfileSettings profile in profileService.Profiles)
            {
                bool active = string.Equals(profile.Id, profileService.ActiveProfileId, StringComparison.OrdinalIgnoreCase);
                bool hasKey = profileService.HasApiKey(profile.Id);
                Console.WriteLine($"{(active ? "*" : " ")} {profile.Id} | {profile.Name} | {profile.ProviderId} | {profile.ModelId} | API key: {(hasKey ? "set" : "not set")}");
            }
            return true;
        }

        if (addProfile)
        {
            string? profileId = GetOptionValue(args, "--profile-id");
            string? providerId = GetOptionValue(args, "--provider");
            string? modelId = GetOptionValue(args, "--model-id");
            if (string.IsNullOrWhiteSpace(profileId) || string.IsNullOrWhiteSpace(providerId) ||
                string.IsNullOrWhiteSpace(modelId))
            {
                throw new InvalidOperationException(
                    "Создание профиля: --model-profile-add --profile-id <id> --provider <providerId> --model-id <modelId> [--name <name>] [--base-url <url>].");
            }

            ProviderDefinition definition = ProviderCatalog.GetRequired(providerId);
            string name = GetOptionValue(args, "--name") ?? definition.Name;
            ModelProfileSettings profile = ModelProfileService.CreateProfile(profileId, name, providerId);
            profile.ModelId = modelId;
            profile.BaseUrl = GetOptionValue(args, "--base-url") ?? profile.BaseUrl;
            await profileService.SaveProfileAsync(profile, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            Console.WriteLine($"[Model] Создан профиль '{profile.Id}' ({profile.ProviderId}).");
            return true;
        }

        if (useProfile)
        {
            string? profileId = GetOptionValue(args, "--model-use");
            if (string.IsNullOrWhiteSpace(profileId))
                profileId = GetOptionValue(args, "--profile");
            if (string.IsNullOrWhiteSpace(profileId))
                throw new InvalidOperationException("Укажите ID профиля: --model-use <profileId>.");

            await profileService.ActivateProfileAsync(profileId, cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"[Model] Активирован профиль: {profileId}");
            return true;
        }

        string targetProfileId = GetOptionValue(args, "--profile") ?? profileService.ActiveProfileId;
        if (setKey)
        {
            Console.Write($"Введите API-ключ для профиля '{targetProfileId}' (ввод скрыт): ");
            string secret = ReadHiddenInput();
            if (string.IsNullOrWhiteSpace(secret))
                throw new InvalidOperationException("Пустой API-ключ не сохранён.");
            profileService.SetApiKey(targetProfileId, secret);
            Console.WriteLine("[Model] API-ключ сохранён в Windows Credential Manager.");
            return true;
        }

        profileService.DeleteApiKey(targetProfileId);
        Console.WriteLine($"[Model] API-ключ профиля '{targetProfileId}' удалён.");
        return true;
    }

    private static string ReadHiddenInput()
    {
        var value = new System.Text.StringBuilder(128);
        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return value.ToString();
            }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (value.Length > 0)
                    value.Length--;
                continue;
            }
            if (!char.IsControl(key.KeyChar))
                value.Append(key.KeyChar);
        }
    }

    private static string? GetOptionValue(string[] args, string option)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], option, StringComparison.OrdinalIgnoreCase) &&
                !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                return args[i + 1];
        }
        return null;
    }

    private static string[] GetOptionValues(string[] args, string option)
    {
        var values = new List<string>();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], option, StringComparison.OrdinalIgnoreCase) &&
                !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                values.Add(args[i + 1]);
        }
        return values.ToArray();
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

    private static async Task<string> DiscoverConfiguredModelsAsync(
        IModelProvider provider,
        string preferredModel,
        CancellationToken cancellationToken)
    {
        if (provider is not IModelDiscovery discoveryProvider)
            return preferredModel;

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
            Console.WriteLine($"[Models] Discovery недоступен ({exception.Message}). Используется модель: {preferredModel}");
            return preferredModel;
        }

        if (!discovery.IsSupported || discovery.Models.Count == 0)
        {
            Console.WriteLine("[Models] Список моделей недоступен. Используется настроенная модель: " + preferredModel);
            return preferredModel;
        }

        string selectedModelId = preferredModel;
        bool found = false;
        foreach (ModelInfo model in discovery.Models)
        {
            Console.WriteLine($"[Models] {model.Id}{(string.IsNullOrWhiteSpace(model.OwnedBy) ? string.Empty : $" ({model.OwnedBy})")}");
            if (string.Equals(model.Id, preferredModel, StringComparison.Ordinal))
            {
                found = true;
                selectedModelId = model.Id;
            }
        }

        if (!found)
        {
            selectedModelId = discovery.Models[0].Id;
            Console.WriteLine($"[Models] Модель '{preferredModel}' не найдена. Автоматически выбрана первая доступная: {selectedModelId}");
        }
        else
        {
            Console.WriteLine($"[Models] Выбрана модель: {selectedModelId}");
        }

        return selectedModelId;
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
