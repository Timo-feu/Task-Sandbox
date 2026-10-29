using System.Threading.Channels;


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

        public void Run()
        {
            Console.WriteLine("[Consumer]: Выполнение задачи ---->");
        }
    }
}