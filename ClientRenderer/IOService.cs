using DanserWrapper;

namespace ClientRenderer;

public static class IOService
{
    public static void CreateVideosDirectoryIfNeeded()
    {
        if (!Directory.Exists(DanserGo.VideosPath))
        {
            Directory.CreateDirectory(DanserGo.VideosPath);
        }
    }
    
    public static bool DanserExists() => File.Exists(DanserGo.DanserGoPath);
}