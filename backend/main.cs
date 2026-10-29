using System;
using System.Threading.Channels;
using System.Threading.Tasks.Dataflow;
using TaskSandbox;

namespace TaskSandbox
{
    public class UserTask
    {
        public int ID {get; set;}
        public string Payload {get; set;}
        public string WorkDir {get; set;}

        public override string ToString()
        {
            return "ID: " + this.ID;           
        }


        public async RunAsync()
        {
            Console.WriteLine("[Consumer]: Выполнение задачи {this.ToStirng()}");

        }
    }

    class Program {
        public static readonly Channel<UserTask> UserTaskChannel = Channel.CreateUnbounded<UserTask>();
        public static async Task Main(string[] args)
        {
            Task consumerTask = Task.Run(async () =>
            {
                await foreach(var task in UserTaskChannel.Reader.ReadAllAsync()) task.Run();
            });

            int TaskIDCnt = 0;
            while (true)
            {
                UserTask myTask = new UserTask{ID = TaskIDCnt++, Payload = $"Hello Wrld", WorkDir = "C:\\"};

                await UserTaskChannel.Writer.WriteAsync(myTask);

                await Task.Delay(5000);
            }
        }
    }
}