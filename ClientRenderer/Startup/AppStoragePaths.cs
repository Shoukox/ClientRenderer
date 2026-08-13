using Velopack.Locators;

namespace ClientRenderer.Startup;

public static class AppStoragePaths
{
    private const string ApplicationName = "ClientRenderer";
    private const string SettingsDirectoryName = "settings";
    private const string DownloadsDirectoryName = "downloads";
    private const string VideosDirectoryName = "videos";

    public static string GetApplicationDataDirectory()
    {
        return GetApplicationRootDirectory();
    }

    public static string GetUserApplicationDataDirectory()
    {
        return GetUserApplicationDataDirectoryCore();
    }

    public static string GetLogsDirectory()
    {
        return GetAppRootDirectory("logs");
    }

    public static string GetVideosDirectory()
    {
        string directoryPath = GetAppRootDirectory(VideosDirectoryName);
        Directory.CreateDirectory(directoryPath);
        return directoryPath;
    }

    public static string GetSettingsDirectory()
    {
        return GetAppRootDirectory(SettingsDirectoryName);
    }

    public static string GetDownloadsDirectory(string subdirectoryName)
    {
        string directoryPath = Path.Combine(GetAppRootDirectory(DownloadsDirectoryName), subdirectoryName);
        Directory.CreateDirectory(directoryPath);
        return directoryPath;
    }

    private static string GetAppRootDirectory(string directoryName)
    {
        return Path.Combine(GetApplicationRootDirectory(), directoryName);
    }

    private static string GetApplicationRootDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            if (VelopackLocator.IsCurrentSet && !VelopackLocator.Current.IsPortable &&
                VelopackLocator.Current.RootAppDir is { } rootAppDirectory)
            {
                return rootAppDirectory;
            }

            return AppContext.BaseDirectory;
        }

        return GetUserApplicationDataDirectoryCore();
    }

    private static string GetUserApplicationDataDirectoryCore()
    {
        if (!OperatingSystem.IsWindows())
        {
            string? xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (!string.IsNullOrWhiteSpace(xdgDataHome))
                return Path.Combine(xdgDataHome, ApplicationName);
        }

        string localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localApplicationData))
            return Path.Combine(localApplicationData, ApplicationName);

        string? homeDirectory = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(homeDirectory))
            return OperatingSystem.IsWindows()
                ? Path.Combine(homeDirectory, "AppData", "Local", ApplicationName)
                : Path.Combine(homeDirectory, ".local", "share", ApplicationName);

        return Path.Combine(Path.GetTempPath(), ApplicationName);
    }
}
