namespace ClientRenderer.Logging
{
    public static class Logger
    {
        public static event Action<string>? MessageLogged;

        public static void Log(string message)
        {
            Write(message, "\u001b[37m");
        }

        public static void LogWarning(string message)
        {
            Write(message, "\u001b[33m");
        }

        public static void LogError(string message)
        {
            Write(message, "\u001b[31m");
        }

        private static void Write(string message, string color)
        {
            MessageLogged?.Invoke(message);
            Console.WriteLine($"\x1b[32m[{DateTime.Now:yyyy-MM-dd HH:mm:ss.ffff}] {color}{message}\x1b[0m");
        }
    }
}
