using Avalonia;
using ClientRenderer.GUI.Services;
using ClientRenderer.Logging;
using System;
using System.Runtime.Versioning;
using Velopack;

namespace ClientRenderer.GUI
{
    internal sealed class Program
    {
        private static readonly string singleInstanceAppId = typeof(Program).Namespace!;

        [STAThread]
        [SupportedOSPlatform("windows")]
        public static void Main(string[] args)
        {
            Logger.Configure("ClientRenderer.GUI");

            VelopackApp.Build()
             .OnFirstRun((v) => { /* Your first run code here */ })
             //.SetLogger(Log)
             .Run();

            using SingleInstanceManager singleInstance = new SingleInstanceManager(singleInstanceAppId);

            if (!singleInstance.IsPrimaryInstance)
            {
                try
                {
                    singleInstance.SignalPrimaryInstance(TimeSpan.FromSeconds(2));
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"Failed to signal the primary instance: {ex.Message}");
                    // If the running instance is shutting down or not ready yet, just exit quietly.
                }

                return;
            }

            singleInstance.StartListening();
            App.SingleInstance = singleInstance;

            try
            {
                BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            }
            finally
            {
                App.SingleInstance = null;
                Logger.CloseAndFlush();
            }
        }

        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace();
    }
}
