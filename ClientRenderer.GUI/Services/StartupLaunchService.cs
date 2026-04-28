using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace ClientRenderer.GUI.Services
{
    public sealed class StartupLaunchService
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppName = "ClientRenderer";

        [SupportedOSPlatformGuard("windows")]
        public bool IsSupported => OperatingSystem.IsWindows();

        public bool IsEnabled()
        {
            if (!IsSupported)
                return false;

            return IsEnabledCore();
        }

        public void SetEnabled(bool enabled)
        {
            if (!IsSupported)
                throw new PlatformNotSupportedException("Run on system startup is only supported on Windows.");

            SetEnabledCore(enabled);
        }

        [SupportedOSPlatform("windows")]
        private static bool IsEnabledCore()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var value = key?.GetValue(AppName) as string;
            return string.Equals(value, BuildCommand(), StringComparison.Ordinal);
        }

        [SupportedOSPlatform("windows")]
        private static void SetEnabledCore(bool enabled)
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);

            if (enabled)
            {
                key.SetValue(AppName, BuildCommand());
            }
            else
            {
                key.DeleteValue(AppName, throwOnMissingValue: false);
            }
        }

        private static string BuildCommand()
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath))
                exePath = Process.GetCurrentProcess().MainModule?.FileName;

            if (string.IsNullOrWhiteSpace(exePath))
                throw new InvalidOperationException("Unable to determine the current executable path.");

            return $"\"{exePath}\"";
        }
    }
}
