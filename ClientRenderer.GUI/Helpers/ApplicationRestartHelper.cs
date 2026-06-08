using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using ClientRenderer.Logging;
using System;
using System.Diagnostics;

namespace ClientRenderer.GUI.Helpers;

public static class ApplicationRestartHelper
{
    public static void RestartApplication()
    {
        try
        {
            string? currentExecutable = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(currentExecutable))
                throw new InvalidOperationException("Could not resolve current executable path.");

            App.SingleInstance?.Dispose();

            ProcessStartInfo startInfo = new(currentExecutable)
            {
                UseShellExecute = true
            };

            foreach (string argument in Environment.GetCommandLineArgs()[1..])
                startInfo.ArgumentList.Add(argument);

            Process.Start(startInfo);

            switch (Application.Current?.ApplicationLifetime)
            {
                case IClassicDesktopStyleApplicationLifetime desktopLifetime:
                    desktopLifetime.TryShutdown();
                    break;
                case IControlledApplicationLifetime controlledLifetime:
                    controlledLifetime.Shutdown();
                    break;
                default:
                    Environment.Exit(0);
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex.ToString());
        }
    }
}
