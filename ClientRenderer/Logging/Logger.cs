using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace ClientRenderer.Logging;

public static class Logger
{
    public static event Action<string>? MessageLogged;

    private static readonly object Sync = new();
    private static bool _configured;

    public static void Configure(string applicationName)
    {
        lock (Sync)
        {
            if (_configured)
                return;

            var logsDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(logsDirectory);

            var outputTemplate = "[{Timestamp:yyyy-MM-dd HH:mm:ss.ffff} {Level:u3}] {Message:lj}{NewLine}{Exception}";
            var errorOutputTemplate = "[{Timestamp:yyyy-MM-dd HH:mm:ss.ffff} {Level:u3}] {Message:lj}{NewLine}{Exception}";

            Serilog.Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .Enrich.WithProperty("Application", applicationName)
                .WriteTo.Console(outputTemplate: outputTemplate)
                .WriteTo.File(
                    path: Path.Combine(logsDirectory, $"{applicationName}-.log"),
                    outputTemplate: outputTemplate,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    shared: true,
                    flushToDiskInterval: TimeSpan.FromSeconds(1))
                .WriteTo.File(
                    path: Path.Combine(logsDirectory, $"{applicationName}-errors-.log"),
                    restrictedToMinimumLevel: LogEventLevel.Error,
                    outputTemplate: errorOutputTemplate,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 30,
                    shared: true,
                    flushToDiskInterval: TimeSpan.FromSeconds(1))
                .CreateLogger();

            _configured = true;
            Serilog.Log.Information("Logger initialized. Writing logs to {LogsDirectory}", logsDirectory);
        }
    }

    public static void CloseAndFlush()
    {
        lock (Sync)
        {
            if (!_configured)
                return;

            try
            {
                Serilog.Log.CloseAndFlush();
            }
            finally
            {
                _configured = false;
            }
        }
    }

    public static void Log(string message)
    {
        Write(LogEventLevel.Information, message);
    }

    public static void LogWarning(string message)
    {
        Write(LogEventLevel.Warning, message);
    }

    public static void LogError(string message)
    {
        Write(LogEventLevel.Error, message);
    }

    public static void LogError(Exception exception, string message)
    {
        MessageLogged?.Invoke(message);
        Serilog.Log.Error(exception, "{Message}", message);
    }

    private static void Write(LogEventLevel level, string message)
    {
        MessageLogged?.Invoke(message);
        Serilog.Log.Write(level, "{Message}", message);
    }
}
