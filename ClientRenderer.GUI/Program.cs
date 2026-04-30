using System;
using System.Runtime.Versioning;
using Avalonia;
using ClientRenderer.GUI.Services;
using ClientRenderer.Logging;
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
            using var singleInstance = new SingleInstanceManager(singleInstanceAppId);

            if (!singleInstance.IsPrimaryInstance)
            {
                try
                {
                    singleInstance.SignalPrimaryInstance(TimeSpan.FromSeconds(2));
                }
                catch
                {
                    // If the running instance is shutting down or not ready yet, just exit quietly.
                }

                return;
            }

            singleInstance.StartListening();
            App.SingleInstance = singleInstance;

            try
            {
                VelopackApp.Build()
                 .OnFirstRun((v) => { /* Your first run code here */ })
                 //.SetLogger(Log)
                 .Run();
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
