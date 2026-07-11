using Velopack.Locators;

namespace ClientRenderer.Startup;

public static class AppStoragePaths
{
    private const string SettingsDirectoryName = "settings";
    private const string DownloadsDirectoryName = "downloads";
    private const string VideosDirectoryName = "videos";

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
        if (VelopackLocator.IsCurrentSet && !VelopackLocator.Current.IsPortable && VelopackLocator.Current.RootAppDir is { } rootAppDirectory)
            return Path.Combine(rootAppDirectory, directoryName);

        return Path.Combine(AppContext.BaseDirectory, directoryName);
    }
}
