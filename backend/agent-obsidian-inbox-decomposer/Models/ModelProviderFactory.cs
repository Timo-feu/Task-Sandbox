using TaskSandbox.Configuration;

namespace TaskSandbox.Models;

public delegate IModelProvider ModelProviderAdapterFactory(
    ModelSettings settings,
    HttpClient httpClient,
    string? apiKey);

/// <summary>Extensible registry of protocol adapters. Provider presets select an adapter by protocol.</summary>
public sealed class ModelProviderRegistry
{
    private readonly Dictionary<ModelApiProtocol, ModelProviderAdapterFactory> _factories = [];

    public ModelProviderRegistry()
    {
        Register(ModelApiProtocol.OpenAiChatCompletions, static (settings, client, apiKey) =>
            new OpenAiCompatibleProvider(
                client,
                settings.ModelId,
                TimeSpan.FromSeconds(settings.RequestTimeoutSeconds),
                apiKey));
    }

    public void Register(ModelApiProtocol protocol, ModelProviderAdapterFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factories[protocol] = factory;
    }

    public bool TryGet(ModelApiProtocol protocol, out ModelProviderAdapterFactory? factory) =>
        _factories.TryGetValue(protocol, out factory);
}

/// <summary>Creates provider adapters from persisted provider settings.</summary>
public sealed class ModelProviderFactory
{
    private readonly ModelProviderRegistry _registry;

    public ModelProviderFactory(ModelProviderRegistry? registry = null) =>
        _registry = registry ?? new ModelProviderRegistry();

    public IModelProvider Create(
        ModelSettings settings,
        HttpClient httpClient,
        string? apiKey = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(httpClient);

        ProviderDefinition definition = ProviderCatalog.GetRequired(settings.ProviderId);
        if (!_registry.TryGet(definition.Protocol, out ModelProviderAdapterFactory? factory) || factory is null)
        {
            throw new NotSupportedException(
                $"Для провайдера '{definition.Name}' ещё не подключён адаптер протокола '{definition.Protocol}'.");
        }

        if (definition.RequiresApiKey && string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                $"Провайдер '{definition.Name}' требует API-ключ. Сначала настройте защищённое хранилище ключей.");
        }

        return factory(settings, httpClient, apiKey);
    }
}
