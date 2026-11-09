using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TaskSandbox;

/// <summary>Adapter for backends implementing the OpenAI chat-completions contract.</summary>
public sealed class OpenAiCompatibleProvider : IModelProvider, IModelDiscovery
{
    private const string ChatCompletionsPath = "v1/chat/completions";
    private const string ModelsPath = "v1/models";
    private const int ReadBufferSize = 16 * 1024;
    private const int MaxEventCharacters = 1024 * 1024;

    private static readonly AdapterCapabilities Capabilities = new(
        ChatCompletions: CapabilitySupport.Supported,
        Streaming: CapabilitySupport.Supported,
        JsonOutput: CapabilitySupport.Unsupported,
        ToolCalling: CapabilitySupport.Unsupported,
        Vision: CapabilitySupport.Unsupported);

    private readonly HttpClient _httpClient;
    private readonly string _model;
    private readonly TimeSpan _requestTimeout;

    public OpenAiCompatibleProvider(
        HttpClient httpClient,
        string model,
        TimeSpan requestTimeout)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model identifier cannot be empty.", nameof(model));

        if (requestTimeout <= TimeSpan.Zero && requestTimeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));

        _httpClient = httpClient;
        _model = model;
        _requestTimeout = requestTimeout;
    }

    public async Task<ModelDiscoveryResult> DiscoverModelsAsync(
        CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CreateTimeoutSource(cancellationToken);
        CancellationToken requestToken = timeoutCts.Token;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ModelsPath);
            using HttpResponseMessage response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                requestToken).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.NotFound
                or HttpStatusCode.MethodNotAllowed
                or HttpStatusCode.NotImplemented)
            {
                return new ModelDiscoveryResult(
                    IsSupported: false,
                    Models: Array.Empty<ModelInfo>(),
                    AdapterCapabilities: Capabilities);
            }

            response.EnsureSuccessStatusCode();

            await using Stream responseStream = await response.Content
                .ReadAsStreamAsync(requestToken)
                .ConfigureAwait(false);

            using JsonDocument document = await JsonDocument.ParseAsync(
                responseStream,
                cancellationToken: requestToken).ConfigureAwait(false);

            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("data", out JsonElement data) ||
                data.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException(
                    "The model-list response does not contain a valid 'data' array.");
            }

            var models = new List<ModelInfo>(data.GetArrayLength());

            foreach (JsonElement item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("id", out JsonElement idElement) ||
                    idElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                string? id = idElement.GetString();
                if (string.IsNullOrWhiteSpace(id))
                    continue;

                string? ownedBy = item.TryGetProperty("owned_by", out JsonElement ownerElement) &&
                                  ownerElement.ValueKind == JsonValueKind.String
                    ? ownerElement.GetString()
                    : null;

                ModelCapabilities modelCapabilities = ReadModelCapabilities(item);
                models.Add(new ModelInfo(id, ownedBy, modelCapabilities));
            }

            return new ModelDiscoveryResult(
                IsSupported: true,
                Models: models.ToArray(),
                AdapterCapabilities: Capabilities);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex) when (timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException("The model discovery request timed out.", ex);
        }
    }

    public async Task StreamAsync(
        ModelRequest request,
        Func<string, ValueTask> onContent,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        ArgumentNullException.ThrowIfNull(onContent);

        using var timeoutCts = CreateTimeoutSource(cancellationToken);
        CancellationToken requestToken = timeoutCts.Token;

        try
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, ChatCompletionsPath)
            {
                Content = JsonContent.Create(CreateWireRequest(request))
            };
            httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

            using HttpResponseMessage response = await _httpClient.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                requestToken).ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            await using Stream responseStream = await response.Content
                .ReadAsStreamAsync(requestToken)
                .ConfigureAwait(false);

            await ReadServerSentEventsAsync(responseStream, onContent, requestToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex) when (timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException("The model request timed out.", ex);
        }
    }

    private CancellationTokenSource CreateTimeoutSource(CancellationToken cancellationToken)
    {
        var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_requestTimeout != Timeout.InfiniteTimeSpan)
            timeoutCts.CancelAfter(_requestTimeout);

        return timeoutCts;
    }

    private static void ValidateRequest(ModelRequest request)
    {
        if (request.Messages is null || request.Messages.Length == 0)
            throw new ArgumentException("At least one chat message is required.", nameof(request));

        if (request.ModelId is not null && string.IsNullOrWhiteSpace(request.ModelId))
            throw new ArgumentException("Model identifier cannot be empty when specified.", nameof(request));

        for (int i = 0; i < request.Messages.Length; i++)
        {
            ModelMessage message = request.Messages[i];
            if (string.IsNullOrWhiteSpace(message.Role) || message.Content is null)
                throw new ArgumentException($"Chat message at index {i} is invalid.", nameof(request));
        }
    }

    private ChatCompletionRequest CreateWireRequest(ModelRequest request)
    {
        var messages = new ChatMessage[request.Messages.Length];

        for (int i = 0; i < messages.Length; i++)
        {
            ModelMessage message = request.Messages[i];
            messages[i] = new ChatMessage(message.Role, message.Content);
        }

        return new ChatCompletionRequest(
            request.ModelId ?? _model,
            messages,
            request.Temperature,
            Stream: true);
    }

    private static ModelCapabilities ReadModelCapabilities(JsonElement model)
    {
        if (!model.TryGetProperty("capabilities", out JsonElement capabilities) ||
            capabilities.ValueKind != JsonValueKind.Object)
        {
            return ModelCapabilities.Unknown;
        }

        return new ModelCapabilities(
            ReadCapability(capabilities, "chat_completions"),
            ReadCapability(capabilities, "streaming"),
            ReadCapability(capabilities, "json_output"),
            ReadCapability(capabilities, "tool_calling"),
            ReadCapability(capabilities, "vision"));
    }

    private static CapabilitySupport ReadCapability(JsonElement capabilities, string name)
    {
        if (!capabilities.TryGetProperty(name, out JsonElement value))
            return CapabilitySupport.Unknown;

        return value.ValueKind switch
        {
            JsonValueKind.True => CapabilitySupport.Supported,
            JsonValueKind.False => CapabilitySupport.Unsupported,
            _ => CapabilitySupport.Unknown
        };
    }

    private static async Task ReadServerSentEventsAsync(
        Stream stream,
        Func<string, ValueTask> onContent,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: ReadBufferSize,
            leaveOpen: true);

        var eventData = new StringBuilder(256);

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                if (!await DispatchEventAsync(eventData, onContent).ConfigureAwait(false))
                    return;

                eventData.Clear();
                continue;
            }

            if (line[0] == ':' || !line.StartsWith("data:", StringComparison.Ordinal))
                continue;

            int dataStart = line.Length > 5 && line[5] == ' ' ? 6 : 5;
            eventData.Append(line, dataStart, line.Length - dataStart);
            eventData.Append('\n');

            if (eventData.Length > MaxEventCharacters)
                throw new InvalidDataException("The model backend sent an oversized SSE event.");
        }

        if (eventData.Length > 0)
            await DispatchEventAsync(eventData, onContent).ConfigureAwait(false);
    }

    private static async Task<bool> DispatchEventAsync(
        StringBuilder eventData,
        Func<string, ValueTask> onContent)
    {
        if (eventData.Length == 0)
            return true;

        if (eventData[eventData.Length - 1] == '\n')
            eventData.Length--;

        if (eventData.Length == 0)
            return true;

        string payload = eventData.ToString();
        eventData.Clear();

        if (string.Equals(payload.Trim(), "[DONE]", StringComparison.Ordinal))
            return false;

        using JsonDocument document = JsonDocument.Parse(payload);
        JsonElement root = document.RootElement;

        if (root.TryGetProperty("error", out JsonElement error))
        {
            string details = error.ValueKind == JsonValueKind.Object &&
                             error.TryGetProperty("message", out JsonElement message) &&
                             message.ValueKind == JsonValueKind.String
                ? message.GetString() ?? "Unknown backend error."
                : error.GetRawText();

            throw new InvalidDataException($"The model backend returned an error: {details}");
        }

        if (!root.TryGetProperty("choices", out JsonElement choices) ||
            choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            return true;

        JsonElement choice = choices[0];
        if (!choice.TryGetProperty("delta", out JsonElement delta) ||
            delta.ValueKind != JsonValueKind.Object ||
            !delta.TryGetProperty("content", out JsonElement contentElement) ||
            contentElement.ValueKind != JsonValueKind.String)
            return true;

        string? content = contentElement.GetString();
        if (!string.IsNullOrEmpty(content))
            await onContent(content).ConfigureAwait(false);

        return true;
    }

    private readonly record struct ChatCompletionRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] ChatMessage[] Messages,
        [property: JsonPropertyName("temperature")] float Temperature,
        [property: JsonPropertyName("stream")] bool Stream);

    private readonly record struct ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);
}
