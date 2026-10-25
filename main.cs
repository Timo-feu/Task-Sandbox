using System;
using UserTask;
using System.Threading.Channels;
using System.Threading.Tasks.Dataflow;

namespace TaskSendbox
{
    class TaskSendbox {

        public static Channel<UserTask> UserTaskChannel = Channel.CreateUnbounded<UserTask>();
        public int Main()
        {
            Channel<UserTask> newTask;
            while(true)
            {
                newTask =  Channel.CreateUnbounded<UserTask>();

                UserTaskChannel.Writer.WriteAsync(newTask);
            }

            while (true)
            {
                UserTask task = UserTaskChannel.Reader.ReadAsync();
                task.Run();
            }

            return 0;
        }


    }
}