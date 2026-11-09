using System.Threading.Channels;

namespace TaskSandbox;

public static class Program
{
    private const int QueueCapacity = 64;
    private const string PreferredModel = "local-model";
    private const string ModelApiBaseAddress = "http://localhost:1234/";
    private static readonly TimeSpan ModelRequestTimeout = TimeSpan.FromMinutes(5);

    private static readonly Channel<UserTask> UserTaskChannel =
        Channel.CreateBounded<UserTask>(new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });

    public static async Task Main()
    {
        using var cancellationSource = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellationSource.Cancel();
        };

        using var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15)
        };

        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(ModelApiBaseAddress),
            Timeout = Timeout.InfiniteTimeSpan
        };

        CancellationToken cancellationToken = cancellationSource.Token;

        try
        {
            IModelProvider modelProvider = await CreateModelProviderAsync(
                httpClient,
                cancellationToken).ConfigureAwait(false);

            string inputFilePath = Path.Combine(Environment.CurrentDirectory, "prompts.txt");
            string targetDirectory = Path.Combine(Environment.CurrentDirectory, "TasksDir");

            Task consumerTask = ConsumeAsync(modelProvider, cancellationToken);

            try
            {
                await EnsureTestFileExistsAsync(inputFilePath, cancellationToken).ConfigureAwait(false);
                await ProduceAsync(inputFilePath, targetDirectory, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                UserTaskChannel.Writer.TryComplete();
            }

            try
            {
                await consumerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Expected during Ctrl+C shutdown.
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine("[System] Получена команда остановки.");
        }

        Console.WriteLine("[System] Обработка очереди завершена.");
    }

    private static async Task<IModelProvider> CreateModelProviderAsync(
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        var discoveryProvider = new OpenAiCompatibleProvider(
            httpClient,
            model: PreferredModel,
            requestTimeout: ModelRequestTimeout);

        ModelDiscoveryResult discovery = await discoveryProvider
            .DiscoverModelsAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!discovery.IsSupported)
        {
            Console.WriteLine("[Models] Endpoint /v1/models не поддерживается. Используется настроенная модель.");
            return discoveryProvider;
        }

        if (discovery.Models.Count == 0)
        {
            Console.WriteLine("[Models] Backend вернул пустой список моделей. Используется настроенная модель.");
            return discoveryProvider;
        }

        Console.WriteLine($"[Models] Обнаружено моделей: {discovery.Models.Count}");
        foreach (ModelInfo model in discovery.Models)
        {
            string owner = string.IsNullOrWhiteSpace(model.OwnedBy) ? string.Empty : $" ({model.OwnedBy})";
            Console.WriteLine($"  - {model.Id}{owner}");
        }

        ModelInfo selectedModel = discovery.Models[0];
        for (int i = 0; i < discovery.Models.Count; i++)
        {
            if (string.Equals(discovery.Models[i].Id, PreferredModel, StringComparison.Ordinal))
            {
                selectedModel = discovery.Models[i];
                break;
            }
        }

        Console.WriteLine($"[Models] Выбрана модель: {selectedModel.Id}");
        PrintCapabilities("Adapter", discovery.AdapterCapabilities);
        PrintCapabilities("Model metadata", selectedModel.Capabilities);

        return new OpenAiCompatibleProvider(
            httpClient,
            model: selectedModel.Id,
            requestTimeout: ModelRequestTimeout);
    }

    private static void PrintCapabilities(string source, AdapterCapabilities capabilities) =>
        PrintCapabilities(source, new ModelCapabilities(
            capabilities.ChatCompletions,
            capabilities.Streaming,
            capabilities.JsonOutput,
            capabilities.ToolCalling,
            capabilities.Vision));

    private static void PrintCapabilities(string source, ModelCapabilities capabilities)
    {
        Console.WriteLine(
            $"[Capabilities:{source}] Chat={Format(capabilities.ChatCompletions)}, " +
            $"Streaming={Format(capabilities.Streaming)}, " +
            $"JSON={Format(capabilities.JsonOutput)}, " +
            $"Tools={Format(capabilities.ToolCalling)}, " +
            $"Vision={Format(capabilities.Vision)}");
    }

    private static string Format(CapabilitySupport support) => support switch
    {
        CapabilitySupport.Supported => "supported",
        CapabilitySupport.Unsupported => "unsupported",
        _ => "unknown"
    };

    private static async Task ProduceAsync(
        string inputFilePath,
        string targetDirectory,
        CancellationToken cancellationToken)
    {
        int taskId = 0;

        await foreach (string line in File.ReadLinesAsync(inputFilePath, cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var task = new UserTask
            {
                Id = taskId++,
                Payload = line,
                WorkDir = targetDirectory
            };

            await UserTaskChannel.Writer.WriteAsync(task, cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"[Producer] Задача {task.Id} добавлена в очередь.");
        }
    }

    private static async Task ConsumeAsync(
        IModelProvider modelProvider,
        CancellationToken cancellationToken)
    {
        await foreach (UserTask task in UserTaskChannel.Reader.ReadAllAsync(cancellationToken))
            await task.RunAsync(modelProvider, cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureTestFileExistsAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        if (File.Exists(filePath))
            return;

        string[] testPrompts =
        [
            "Напиши короткое хокку про программиста на C#.",
            "Объясни, что такое асинхронность простыми словами (в двух предложениях).",
            "Назови 3 главные причины использовать структуры (struct) вместо классов (class).",
            "Придумай смешное название для кофейни, которую открыли айтишники."
        ];

        await File.WriteAllLinesAsync(filePath, testPrompts, cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"[System] Создан тестовый файл: {filePath}");
    }
}
