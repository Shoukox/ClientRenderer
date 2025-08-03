using DanserWrapper;

namespace ClientRenderer;

public static class IOService
{
    public static void CreateDirectoriesIfNeeded()
    {
        if (!Directory.Exists(DanserGo.VideosPath))
        {
            Directory.CreateDirectory(DanserGo.VideosPath);
        }
        
        if (!Directory.Exists(DanserGo.SongsPath))
        {
            Directory.CreateDirectory(DanserGo.SongsPath);
        }
    }
    
    public static bool DanserExists() => File.Exists(DanserGo.DanserGoPath);
}