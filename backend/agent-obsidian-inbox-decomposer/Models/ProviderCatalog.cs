namespace TaskSandbox.Models;

public enum ModelApiProtocol : byte
{
    OpenAiChatCompletions,
    AnthropicMessages,
    GoogleGenerateContent
}

public sealed record ProviderDefinition(
    string Id,
    string Name,
    ModelApiProtocol Protocol,
    string DefaultBaseUrl,
    bool RequiresApiKey,
    bool SupportsModelDiscovery);

/// <summary>Built-in provider presets. Multiple services share the OpenAI-compatible adapter.</summary>
public static class ProviderCatalog
{
    private static readonly ProviderDefinition[] Definitions =
    [
        new("openai-compatible", "OpenAI-compatible custom endpoint", ModelApiProtocol.OpenAiChatCompletions, "http://localhost:1234/", false, true),
        new("lm-studio", "LM Studio", ModelApiProtocol.OpenAiChatCompletions, "http://localhost:1234/", false, true),
        new("ollama", "Ollama (OpenAI compatible)", ModelApiProtocol.OpenAiChatCompletions, "http://localhost:11434/", false, true),
        new("vllm", "vLLM", ModelApiProtocol.OpenAiChatCompletions, "http://localhost:8000/", false, true),
        new("localai", "LocalAI", ModelApiProtocol.OpenAiChatCompletions, "http://localhost:8080/", false, true),
        new("openai", "OpenAI", ModelApiProtocol.OpenAiChatCompletions, "https://api.openai.com/", true, true),
        new("openrouter", "OpenRouter", ModelApiProtocol.OpenAiChatCompletions, "https://openrouter.ai/api/", true, true),
        new("deepseek", "DeepSeek", ModelApiProtocol.OpenAiChatCompletions, "https://api.deepseek.com/", true, true),
        new("groq", "Groq", ModelApiProtocol.OpenAiChatCompletions, "https://api.groq.com/openai/", true, true),
        new("together", "Together AI", ModelApiProtocol.OpenAiChatCompletions, "https://api.together.xyz/", true, true)
    ];

    public static IReadOnlyList<ProviderDefinition> All => Definitions;

    public static bool TryGet(string id, out ProviderDefinition? definition)
    {
        foreach (ProviderDefinition item in Definitions)
        {
            if (string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                definition = item;
                return true;
            }
        }

        definition = null;
        return false;
    }

    public static ProviderDefinition GetRequired(string id) =>
        TryGet(id, out ProviderDefinition? definition)
            ? definition!
            : throw new InvalidOperationException($"Неизвестный провайдер модели '{id}'.");
}
