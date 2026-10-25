using System.Threading.Channels;


namespace UserTask
{
    public class UserTask
    {
        public int ID {get; set;}
        public string Playload {get; set;}
        public string WorkDir {get; set;}

        public UserTask(int id = 0, string playload, string workDir)
        {
            this.ID = id;
            this.Playload = playload;
            this.WorkDir = workDir;
        }

        public override string ToString()
        {
            return "ID: " + this.ID;           
        }

        public static bool IsRun()
        {
            if (this.ID = 0) return true;
            
            return false;
        }

        public static void Run()
        {
            if (this.IsRun())
            {
                // отправка задачи иишке
            }
            else
            {
                Console.Write("Агент занят выполнением задачи " + this.ID);
            }
        }
    }
}