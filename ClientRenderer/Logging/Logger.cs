namespace ClientRenderer.Logging
{
    public static class Logger
    {
        public static void Log(string message)
        {
            Console.WriteLine($"\x1b[32m[{DateTime.Now:yyyy-MM-dd HH:mm:ss.ffff}] \x1b[37m{message}\x1b[0m");
        }

        public static void LogWarning(string message)
        {
            Console.WriteLine($"\x1b[32m[{DateTime.Now:yyyy-MM-dd HH:mm:ss.ffff}] \u001b[33m{message}\x1b[0m");
        }

        public static void LogError(string message)
        {
            Console.WriteLine($"\x1b[32m[{DateTime.Now:yyyy-MM-dd HH:mm:ss.ffff}] \u001b[31m{message}\x1b[0m");
        }
    }
}
