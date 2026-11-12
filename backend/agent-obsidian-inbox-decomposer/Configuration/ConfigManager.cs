using System.Text.Json;
using System.Text.Json.Serialization;

namespace TaskSandbox.Configuration;

/// <summary>
/// Loads the single application settings file from LocalAppData.
/// Invalid JSON or invalid settings are backed up and replaced with defaults.
/// I/O and permission errors are intentionally propagated to the application boundary.
/// </summary>
public static class ConfigManager
{
    private const string ApplicationDirectoryName = "LocalCodingAgent";
    private const string SettingsFileName = "settings.json";
    private const string BackupFileName = "settings.backup.json";
    private const int MaxSettingsBytes = 256 * 1024;
    private const int MaxWorkspaces = 64;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip
    };

    public static string ApplicationDirectoryPath => GetApplicationDirectoryPath();
    public static string SettingsFilePath => Path.Combine(ApplicationDirectoryPath, SettingsFileName);

    public static async Task<ConfigLoadResult> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        string directory = GetApplicationDirectoryPath();
        Directory.CreateDirectory(directory);

        string settingsPath = Path.Combine(directory, SettingsFileName);
        if (!File.Exists(settingsPath))
        {
            AgentSettings defaults = AgentSettings.CreateDefault();
            ValidateAndNormalize(defaults);
            await WriteAtomicAsync(settingsPath, defaults, cancellationToken).ConfigureAwait(false);

            return new ConfigLoadResult(defaults, settingsPath, WasCreated: true,
                WasReset: false, BackupPath: null, ResetReason: null);
        }

        try
        {
            var info = new FileInfo(settingsPath);
            if (info.Length > MaxSettingsBytes)
                throw new InvalidDataException($"Файл настроек превышает лимит {MaxSettingsBytes} байт.");

            string json = await File.ReadAllTextAsync(settingsPath, cancellationToken).ConfigureAwait(false);
            AgentSettings? settings = JsonSerializer.Deserialize<AgentSettings>(json, JsonOptions);
            if (settings is null)
                throw new InvalidDataException("Файл настроек не содержит JSON-объект.");

            ValidateAndNormalize(settings);
            return new ConfigLoadResult(settings, settingsPath, WasCreated: false,
                WasReset: false, BackupPath: null, ResetReason: null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            string backupPath = BackupInvalidSettings(settingsPath, cancellationToken);

            AgentSettings defaults = AgentSettings.CreateDefault();
            ValidateAndNormalize(defaults);
            await WriteAtomicAsync(settingsPath, defaults, cancellationToken).ConfigureAwait(false);

            return new ConfigLoadResult(defaults, settingsPath, WasCreated: false,
                WasReset: true, BackupPath: backupPath, ResetReason: exception.Message);
        }
    }

    private static string GetApplicationDirectoryPath()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
            throw new InvalidOperationException("Windows LocalAppData directory is unavailable.");

        return Path.Combine(localAppData, ApplicationDirectoryName);
    }

    private static void ValidateAndNormalize(AgentSettings settings)
    {
        if (settings.SchemaVersion != AgentSettings.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Неподдерживаемая версия настроек: {settings.SchemaVersion}. " +
                $"Текущая версия схемы: {AgentSettings.CurrentSchemaVersion}.");
        }

        settings.Obsidian ??= new ObsidianSettings();
        settings.Workspaces ??= new WorkspaceSettings();
        settings.Model ??= new ModelSettings();
        settings.Prompts ??= new PromptSettings();
        settings.Runtime ??= new RuntimeSettings();
        settings.Workspaces.Registered ??= [];

        settings.Obsidian.VaultPath = NormalizeOptionalPath(
            settings.Obsidian.VaultPath, "obsidian.vaultPath");

        settings.Workspaces.DefaultId = (settings.Workspaces.DefaultId ?? string.Empty).Trim();
        if (settings.Workspaces.DefaultId.Length > 80 ||
            !IsValidIdentifier(settings.Workspaces.DefaultId))
        {
            throw new InvalidDataException(
                "workspaces.defaultId должен содержать от 1 до 80 символов: латинские буквы, цифры, '-' или '_'.");
        }

        if (settings.Workspaces.Registered.Count > MaxWorkspaces)
            throw new InvalidDataException($"Количество Workspace не может превышать {MaxWorkspaces}.");

        var workspaceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (WorkspaceEntry workspace in settings.Workspaces.Registered)
        {
            if (workspace is null)
                throw new InvalidDataException("Список workspaces.registered содержит пустую запись.");

            workspace.Id = (workspace.Id ?? string.Empty).Trim();
            if (!IsValidIdentifier(workspace.Id) || workspace.Id.Length > 80)
                throw new InvalidDataException("Каждый Workspace должен иметь корректный id (латиница, цифры, '-' или '_').");
            if (!workspaceIds.Add(workspace.Id))
                throw new InvalidDataException($"Workspace id '{workspace.Id}' указан более одного раза.");

            workspace.Path = NormalizeOptionalPath(workspace.Path, $"workspaces.registered[{workspace.Id}].path");
            if (workspace.Path.Length == 0)
                throw new InvalidDataException($"Для Workspace '{workspace.Id}' не задан path.");
        }

        if (settings.Workspaces.Registered.Count > 0 &&
            !workspaceIds.Contains(settings.Workspaces.DefaultId))
        {
            throw new InvalidDataException(
                $"Workspace по умолчанию '{settings.Workspaces.DefaultId}' отсутствует в workspaces.registered.");
        }

        settings.Model.BaseUrl = (settings.Model.BaseUrl ?? string.Empty).Trim();
        if (!Uri.TryCreate(settings.Model.BaseUrl, UriKind.Absolute, out Uri? modelUri) ||
            modelUri is null ||
            (modelUri.Scheme != Uri.UriSchemeHttp && modelUri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(modelUri.UserInfo) ||
            !string.IsNullOrEmpty(modelUri.Query) ||
            !string.IsNullOrEmpty(modelUri.Fragment))
        {
            throw new InvalidDataException("model.baseUrl должен быть абсолютным HTTP/HTTPS URL без логина и пароля.");
        }

        settings.Model.BaseUrl = modelUri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? modelUri.AbsoluteUri
            : modelUri.AbsoluteUri + "/";

        settings.Model.ModelId = (settings.Model.ModelId ?? string.Empty).Trim();
        if (settings.Model.ModelId.Length is 0 or > 256)
            throw new InvalidDataException("model.modelId должен содержать от 1 до 256 символов.");
        if (settings.Model.RequestTimeoutSeconds is < 10 or > 1800)
            throw new InvalidDataException("model.requestTimeoutSeconds должен находиться в диапазоне 10–1800.");

        settings.Prompts.DirectoryPath = NormalizeOptionalPath(
            settings.Prompts.DirectoryPath, "prompts.directoryPath");
        if (settings.Runtime.InboxPollIntervalSeconds is < 1 or > 300)
            throw new InvalidDataException("runtime.inboxPollIntervalSeconds должен находиться в диапазоне 1–300.");
        if (settings.Runtime.MaxConcurrentTasks is < 1 or > 16)
            throw new InvalidDataException("runtime.maxConcurrentTasks должен находиться в диапазоне 1–16.");
        if (settings.Runtime.MaxCorrectionAttempts is < 0 or > 10)
            throw new InvalidDataException("runtime.maxCorrectionAttempts должен находиться в диапазоне 0–10.");
        if (settings.Runtime.ProcessTimeoutSeconds is < 10 or > 1800)
            throw new InvalidDataException("runtime.processTimeoutSeconds должен находиться в диапазоне 10–1800.");
        if (settings.Runtime.MaxProcessOutputCharacters is < 4096 or > 262144)
            throw new InvalidDataException("runtime.maxProcessOutputCharacters должен находиться в диапазоне 4096–262144.");
    }

    private static bool IsValidIdentifier(string value)
    {
        if (value.Length == 0)
            return false;

        foreach (char character in value)
        {
            if (!(char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
                return false;
        }

        return true;
    }

    private static string NormalizeOptionalPath(string? value, string settingName)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        try
        {
            return Path.GetFullPath(value.Trim());
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException($"Некорректный путь в настройке {settingName}.", exception);
        }
    }

    private static string BackupInvalidSettings(
        string settingsPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string backupPath = Path.Combine(Path.GetDirectoryName(settingsPath)!, BackupFileName);
        string temporaryBackupPath = backupPath + ".tmp";
        File.Copy(settingsPath, temporaryBackupPath, overwrite: true);
        File.Move(temporaryBackupPath, backupPath, overwrite: true);
        return backupPath;
    }

    private static async Task WriteAtomicAsync(
        string destinationPath,
        AgentSettings settings,
        CancellationToken cancellationToken)
    {
        string temporaryPath = destinationPath + ".tmp";

        await using (var stream = new FileStream(
                         temporaryPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         bufferSize: 4096,
                         options: FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporaryPath, destinationPath, overwrite: true);
    }
}
