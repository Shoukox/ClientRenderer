using ClientRenderer.Logging;
using Microsoft.Win32;
using System.Runtime.Versioning;

namespace ClientRenderer.Helpers;

public static class WindowsGpuPreferenceHelper
{
    private const string UserGpuPreferencesRegistryPath = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private const string HighPerformancePreferenceValue = "GpuPreference=2;";

    public static void SetHighPerformanceForExecutables(IEnumerable<string> executablePaths)
    {
        if (!OperatingSystem.IsWindows())
            return;

        SetHighPerformanceForExecutablesWindows(executablePaths);
    }

    [SupportedOSPlatform("windows")]
    private static void SetHighPerformanceForExecutablesWindows(IEnumerable<string> executablePaths)
    {
        var normalizedPaths = executablePaths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalizedPaths.Length == 0)
            return;

        using var gpuPreferencesKey = Registry.CurrentUser.CreateSubKey(UserGpuPreferencesRegistryPath, writable: true);
        if (gpuPreferencesKey is null)
            throw new InvalidOperationException($"Failed to open or create registry key: HKCU\\{UserGpuPreferencesRegistryPath}");

        foreach (var executablePath in normalizedPaths)
        {
            gpuPreferencesKey.SetValue(executablePath, HighPerformancePreferenceValue, RegistryValueKind.String);
            Logger.Log($"Configured high-performance GPU preference for: {executablePath}");
        }
    }
}
