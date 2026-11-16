using TaskSandbox.Models;

namespace TaskSandbox.Configuration;

/// <summary>Manages saved model connections and their separately stored credentials.</summary>
public sealed class ModelProfileService
{
    private const string CredentialPrefix = "LocalCodingAgent/ModelProviders/";

    private readonly AgentSettings _settings;
    private readonly IModelSecretStore _secretStore;

    public ModelProfileService(AgentSettings settings, IModelSecretStore secretStore)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
    }

    public IReadOnlyList<ModelProfileSettings> Profiles => _settings.Model.Profiles;
    public string ActiveProfileId => _settings.Model.ActiveProfileId;

    public static string GetCredentialTarget(string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId) || profileId.Length > 80)
            throw new ArgumentException("Некорректный ID профиля модели.", nameof(profileId));
        foreach (char character in profileId)
        {
            if (!(char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
                throw new ArgumentException("ID профиля может содержать только латиницу, цифры, '-' и '_'.", nameof(profileId));
        }
        return CredentialPrefix + profileId.ToLowerInvariant();
    }

    public string? GetActiveApiKey() => GetApiKey(_settings.Model.ActiveProfileId);

    public string? GetApiKey(string profileId)
    {
        _ = FindProfile(profileId);
        return _secretStore.Get(GetCredentialTarget(profileId));
    }

    public bool HasApiKey(string profileId) => !string.IsNullOrEmpty(GetApiKey(profileId));

    public void SetApiKey(string profileId, string apiKey)
    {
        _ = FindProfile(profileId);
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("API-ключ не может быть пустым.", nameof(apiKey));
        _secretStore.Set(GetCredentialTarget(profileId), apiKey.Trim());
    }

    public void DeleteApiKey(string profileId)
    {
        _ = FindProfile(profileId);
        _secretStore.Delete(GetCredentialTarget(profileId));
    }

    public async Task SaveProfileAsync(
        ModelProfileSettings profile,
        bool activate = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ModelProfileSettings copy = Clone(profile);
        int existingIndex = -1;
        for (int i = 0; i < _settings.Model.Profiles.Count; i++)
        {
            if (string.Equals(_settings.Model.Profiles[i].Id, copy.Id, StringComparison.OrdinalIgnoreCase))
            {
                existingIndex = i;
                break;
            }
        }

        if (existingIndex >= 0)
            _settings.Model.Profiles[existingIndex] = copy;
        else
            _settings.Model.Profiles.Add(copy);

        if (activate || _settings.Model.Profiles.Count == 1)
            ActivateInMemory(copy);

        await ConfigManager.SaveAsync(_settings, cancellationToken).ConfigureAwait(false);
    }

    public async Task ActivateProfileAsync(
        string profileId,
        CancellationToken cancellationToken = default)
    {
        ModelProfileSettings profile = FindProfile(profileId);
        ActivateInMemory(profile);
        await ConfigManager.SaveAsync(_settings, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteProfileAsync(
        string profileId,
        CancellationToken cancellationToken = default)
    {
        int index = -1;
        for (int i = 0; i < _settings.Model.Profiles.Count; i++)
        {
            if (string.Equals(_settings.Model.Profiles[i].Id, profileId, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }

        if (index < 0)
            return;
        if (_settings.Model.Profiles.Count == 1)
            throw new InvalidOperationException("Нельзя удалить последний профиль модели.");

        string removedId = _settings.Model.Profiles[index].Id;
        _settings.Model.Profiles.RemoveAt(index);
        if (string.Equals(_settings.Model.ActiveProfileId, removedId, StringComparison.OrdinalIgnoreCase))
            ActivateInMemory(_settings.Model.Profiles[0]);

        await ConfigManager.SaveAsync(_settings, cancellationToken).ConfigureAwait(false);
        _secretStore.Delete(GetCredentialTarget(removedId));
    }

    public static ModelProfileSettings CreateProfile(string id, string name, string providerId)
    {
        ProviderDefinition provider = ProviderCatalog.GetRequired(providerId);
        return new ModelProfileSettings
        {
            Id = id,
            Name = name,
            ProviderId = provider.Id,
            BaseUrl = provider.DefaultBaseUrl,
            ModelId = "model-id",
            RequestTimeoutSeconds = 300
        };
    }

    private ModelProfileSettings FindProfile(string profileId)
    {
        foreach (ModelProfileSettings profile in _settings.Model.Profiles)
        {
            if (string.Equals(profile.Id, profileId, StringComparison.OrdinalIgnoreCase))
                return profile;
        }
        throw new KeyNotFoundException($"Профиль модели '{profileId}' не найден.");
    }

    private void ActivateInMemory(ModelProfileSettings profile)
    {
        _settings.Model.ActiveProfileId = profile.Id;
        _settings.Model.ProviderId = profile.ProviderId;
        _settings.Model.BaseUrl = profile.BaseUrl;
        _settings.Model.ModelId = profile.ModelId;
        _settings.Model.RequestTimeoutSeconds = profile.RequestTimeoutSeconds;
    }

    private static ModelProfileSettings Clone(ModelProfileSettings source) => new()
    {
        Id = source.Id,
        Name = source.Name,
        ProviderId = source.ProviderId,
        BaseUrl = source.BaseUrl,
        ModelId = source.ModelId,
        RequestTimeoutSeconds = source.RequestTimeoutSeconds
    };
}
