using System.Text;

namespace TaskSandbox;

/// <summary>A provider-independent chat message.</summary>
public readonly record struct ModelMessage(string Role, string Content);

/// <summary>A provider-independent chat completion request.</summary>
/// <param name="ModelId">Optional model override. Null uses the provider's configured default.</param>
public readonly record struct ModelRequest(
    ModelMessage[] Messages,
    float Temperature = 0.7f,
    string? ModelId = null);

public enum CapabilitySupport : byte
{
    Unknown,
    Supported,
    Unsupported
}

/// <summary>Capabilities declared for an individual model by the backend, when available.</summary>
public readonly record struct ModelCapabilities(
    CapabilitySupport ChatCompletions,
    CapabilitySupport Streaming,
    CapabilitySupport JsonOutput,
    CapabilitySupport ToolCalling,
    CapabilitySupport Vision)
{
    public static ModelCapabilities Unknown => new(
        CapabilitySupport.Unknown,
        CapabilitySupport.Unknown,
        CapabilitySupport.Unknown,
        CapabilitySupport.Unknown,
        CapabilitySupport.Unknown);
}

/// <summary>Model metadata returned by the provider's model-list endpoint.</summary>
public readonly record struct ModelInfo(
    string Id,
    string? OwnedBy,
    ModelCapabilities Capabilities);

/// <summary>Result of trying the optional model-list endpoint.</summary>
public readonly record struct ModelDiscoveryResult(
    bool IsSupported,
    IReadOnlyList<ModelInfo> Models,
    AdapterCapabilities AdapterCapabilities);

/// <summary>Capabilities implemented by the adapter itself, not guarantees about every model.</summary>
public readonly record struct AdapterCapabilities(
    CapabilitySupport ChatCompletions,
    CapabilitySupport Streaming,
    CapabilitySupport JsonOutput,
    CapabilitySupport ToolCalling,
    CapabilitySupport Vision);

/// <summary>Abstraction over a chat-completion backend.</summary>
public interface IModelProvider
{
    /// <summary>Streams generated text fragments to the supplied callback.</summary>
    Task StreamAsync(
        ModelRequest request,
        Func<string, ValueTask> onContent,
        CancellationToken cancellationToken = default);

    /// <summary>Gets a complete response by collecting streamed fragments.</summary>
    async Task<string> CompleteAsync(
        ModelRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = new StringBuilder(1024);

        await StreamAsync(
            request,
            content =>
            {
                result.Append(content);
                return ValueTask.CompletedTask;
            },
            cancellationToken).ConfigureAwait(false);

        if (result.Length == 0)
            throw new InvalidDataException("The model backend returned an empty completion.");

        return result.ToString();
    }
}

/// <summary>Optional model discovery supported by providers that expose a model-list endpoint.</summary>
public interface IModelDiscovery
{
    Task<ModelDiscoveryResult> DiscoverModelsAsync(
        CancellationToken cancellationToken = default);
}
