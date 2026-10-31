using System;
using System.Threading.Channels;
using System.IO;
using System.Threading.Tasks;

namespace TaskSandbox
{
    public class UserTask
    {
        public int ID {get; set;}
        public string Payload {get; set;} = string.Empty;
        public string WorkDir {get; set;} = string.Empty;

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
            if(string.IsNullOrWhiteSpace(this.WorkDir))                     return TaskValidationResult.EmptyPath;
            if (this.WorkDir.IndexOfAny(Path.GetInvalidPathChars()) >= 0)   return TaskValidationResult.InvalidCharacters;
            if (!Directory.Exists(this.WorkDir))                            return TaskValidationResult.DirectoryNotFound;
            
            return TaskValidationResult.Success;
        }

        public async void RunAsync()
        {
            TaskValidationResult status = this.IsValidDirectory();
            
            int idx = (int)status;
            Console.WriteLine($"[Consumer] Task {this.ID}: {statusMessages[idx]}");

            if (status == TaskValidationResult.EmptyPath || status == TaskValidationResult.InvalidCharacters)
            {
                Console.WriteLine($"[Consumer] Задача {this.ID} отклонена.");
                return; 
            }

            if (status == TaskValidationResult.DirectoryNotFound)
            {
                Directory.CreateDirectory(this.WorkDir);
                Console.WriteLine($"[Consumer] Задача {this.ID}: Директория '{this.WorkDir}' успешно создана. Задача принята в разработку.");
            }

            await File.WriteAllTextAsync(Path.Combine(this.WorkDir, $"task_{this.ID}.txt"), this.Payload);
        }

        public async void WriteAsyncFile()
        {
            TaskValidationResult status = this.IsValidDirectory();
            
            int idx = (int)status;
            Console.WriteLine($"[Consumer] Task {this.ID}: {statusMessages[idx]}");

            if (status == TaskValidationResult.EmptyPath || status == TaskValidationResult.InvalidCharacters)
            {
                Console.WriteLine($"[Consumer] Задача {this.ID} отклонена.");
                return; 
            }

            if (status == TaskValidationResult.DirectoryNotFound)
            {
                Directory.CreateDirectory(this.WorkDir);
                Console.WriteLine($"[Consumer] Задача {this.ID}: Директория '{this.WorkDir}' успешно создана. Задача принята в разработку.");
            }

            await File.WriteAllTextAsync(Path.Combine(this.WorkDir, $"task_{this.ID}.txt"), this.Payload);
        }
    }

    class Program {
        public static readonly Channel<UserTask> UserTaskChannel = Channel.CreateUnbounded<UserTask>();
        public static async Task Main(string[] args)
        {
            Task consumerTask = Task.Run(async () =>
            {
                await foreach(var task in UserTaskChannel.Reader.ReadAllAsync()) 
                    task.RunAsync();
            });

            int TaskIDCnt = 0;
            while (true)
            {   
                string targetDir = Path.Combine(Environment.CurrentDirectory, "TasksDir");

                UserTask myTask = new UserTask{
                    ID = TaskIDCnt++, 
                    Payload = $"Hello Wrld", 
                    WorkDir = targetDir
                };

                await UserTaskChannel.Writer.WriteAsync(myTask);

                await Task.Delay(5000);
            }
        }
    }
}