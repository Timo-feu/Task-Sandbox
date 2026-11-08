using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace TaskSandbox
{
    // Используем readonly record struct для DTO, чтобы избежать аллокаций в куче (Heap) и не нагружать GC
    public readonly record struct ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    public readonly record struct ChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] ChatMessage[] Messages,
        [property: JsonPropertyName("temperature")] float Temperature);

    public readonly record struct ChatChoice(
        [property: JsonPropertyName("message")] ChatMessage Message);

    public readonly record struct ChatResponse(
        [property: JsonPropertyName("choices")] ChatChoice[] Choices);

    public class UserTask
    {
        public int ID { get; set; }
        public string Payload { get; set; } = string.Empty;
        public string WorkDir { get; set; } = string.Empty;
        public StatusT Status { get; set; } = StatusT.Proceed;

        public enum StatusT : byte
        {
            Proceed,
            Ready,
            InProgress,
            Rejected
        }

        public enum TaskValidationResult : byte
        {
            Success = 0,
            EmptyPath = 1,
            InvalidCharacters = 2,
            DirectoryNotFound = 3
        }

        public static readonly string[] statusMessages = new string[]
        {
            "Задача принята в разработку.",
            "Ошибка: Путь к директории пустой.",
            "Ошибка: Путь содержит запрещенные символы.",
            "Ошибка: Директория не существует на диске."
        };

        public TaskValidationResult IsValidDirectory()
        {
            if (string.IsNullOrWhiteSpace(this.WorkDir)) return TaskValidationResult.EmptyPath;
            if (this.WorkDir.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return TaskValidationResult.InvalidCharacters;
            if (!Directory.Exists(this.WorkDir)) return TaskValidationResult.DirectoryNotFound;

            return TaskValidationResult.Success;
        }

        public async Task RunAsync()
        {
            // Сначала проверяем/создаем папку и записываем исходный промпт
            await this.WriteAsyncFile();
            
            if (this.Status == StatusT.Rejected) return;

            this.Status = StatusT.InProgress;
            Console.WriteLine($"[Consumer] Задача {this.ID}: Отправка запроса в LM Studio...");
            
            await this.RunPromtLLM();
            
            this.Status = StatusT.Ready;
            Console.WriteLine($"[Consumer] Задача {this.ID} успешно завершена.");
        }

        public async Task WriteAsyncFile()
        {
            TaskValidationResult status = this.IsValidDirectory();

            int idx = (int)status;
            Console.WriteLine($"[Consumer] Task {this.ID}: {statusMessages[idx]}");

            if (status == TaskValidationResult.EmptyPath || status == TaskValidationResult.InvalidCharacters)
            {
                Console.WriteLine($"[Consumer] Задача {this.ID} отклонена.");
                this.Status = StatusT.Rejected;
                return;
            }

            if (status == TaskValidationResult.DirectoryNotFound)
            {
                Directory.CreateDirectory(this.WorkDir);
                Console.WriteLine($"[Consumer] Задача {this.ID}: Директория '{this.WorkDir}' успешно создана.");
            }

            string filePath = Path.Combine(this.WorkDir, $"task_{this.ID}.txt");
            await File.WriteAllTextAsync(filePath, $"--- ЗАДАЧА ---\n{this.Payload}\n\n");
        }

        public async Task RunPromtLLM()
        {
            // Формируем структуру запроса по стандарту OpenAI API (используется в LM Studio)
            var requestData = new ChatRequest(
                Model: "local-model", // LM Studio использует текущую загруженную модель
                Messages: new[]
                {
                    new ChatMessage("system", "Ты полезный ИИ-ассистент. Отвечай четко, кратко и на русском языке."),
                    new ChatMessage("user", this.Payload)
                },
                Temperature: 0.7f
            );

            // PostAsJsonAsync сразу пишет в поток сети без создания промежуточных строк
            using HttpResponseMessage response = await Program.LlmClient.PostAsJsonAsync("/v1/chat/completions", requestData);

            string answer = "Ошибка: Модель не вернула корректный ответ (проверьте консоль LM Studio).";

            // Вместо try-catch проверяем статус-код ответа
            if (response.IsSuccessStatusCode)
            {
                // Десериализация напрямую из сетевого потока
                var result = await response.Content.ReadFromJsonAsync<ChatResponse>();
                if (result.Choices is { Length: > 0 })
                {
                    answer = result.Choices[0].Message.Content;
                }
            }

            await this.WriteAyncForFile(answer);
        }

        public async Task WriteAyncForFile(string answer)
        {
            string filePath = Path.Combine(this.WorkDir, $"task_{this.ID}.txt");
            // Дописываем ответ в конец файла, чтобы сохранить и вопрос, и ответ
            await File.AppendAllTextAsync(filePath, $"--- ОТВЕТ МОДЕЛИ ---\n{answer}");
        }
    }

    class Program
    {
        public static readonly Channel<UserTask> UserTaskChannel = Channel.CreateUnbounded<UserTask>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true } // Оптимизация канала под 1 чтеца и 1 писателя
        );

        // Максимально оптимизированный HttpClient для переиспользования сокетов
        public static readonly HttpClient LlmClient = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            UseProxy = false // Отключаем проверку системного прокси для ускорения локальных запросов
        })
        {
            BaseAddress = new Uri("http://localhost:1234"), // Стандартный порт LM Studio
            Timeout = TimeSpan.FromMinutes(5) // Запас времени на случай долгой генерации
        };

        public static async Task Main(string[] args)
        {
            string inputFilePath = Path.Combine(Environment.CurrentDirectory, "prompts.txt");
            string targetDir = Path.Combine(Environment.CurrentDirectory, "TasksDir");

            // 1. Создаем тестовый файл с задачами, если его нет
            await EnsureTestFileExistsAsync(inputFilePath);

            // 2. Запускаем Consumer (Потребитель)
            Task consumerTask = Task.Run(async () =>
            {
                await foreach (var task in UserTaskChannel.Reader.ReadAllAsync())
                {
                    await task.RunAsync();
                }
            });

            // 3. Producer (Производитель) - читает файл построчно (асинхронный стрим)
            int taskIDCnt = 0;
            await foreach (string line in File.ReadLinesAsync(inputFilePath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                var myTask = new UserTask
                {
                    ID = taskIDCnt++,
                    Payload = line,
                    WorkDir = targetDir
                };

                await UserTaskChannel.Writer.WriteAsync(myTask);
                Console.WriteLine($"[Producer] Задача {myTask.ID} добавлена в очередь.");
            }

            // Сигнализируем каналу, что новых задач больше не будет
            UserTaskChannel.Writer.Complete();

            // Ждем, пока Consumer обработает все задачи из очереди и завершит работу
            await consumerTask;
            Console.WriteLine("\n[System] Все задачи из файла успешно обработаны!");
        }

        private static async Task EnsureTestFileExistsAsync(string filePath)
        {
            if (!File.Exists(filePath))
            {
                string[] testPrompts = new[]
                {
                    "Напиши короткое хокку про программиста на C#.",
                    "Объясни, что такое асинхронность простыми словами (в двух предложениях).",
                    "Назови 3 главные причины использовать структуры (struct) вместо классов (class).",
                    "Придумай смешное название для кофейни, которую открыли айтишники."
                };

                await File.WriteAllLinesAsync(filePath, testPrompts);
                Console.WriteLine($"[System] Создан тестовый файл с задачами: {filePath}");
            }
        }
    }
}