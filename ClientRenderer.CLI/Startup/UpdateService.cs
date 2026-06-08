using ClientRenderer.CLI.Abstractions;
using ClientRenderer.Logging;
using Velopack;
using Velopack.Sources;

namespace ClientRenderer.CLI.Startup;

public sealed class UpdateService : IUpdateService
{
    public async Task CheckForUpdatesAsync(string[] args)
    {
        Logger.Log("Searching for updates...");
        try
        {
            UpdateManager mgr = new UpdateManager(
                new GithubSource(
                    repoUrl: "https://github.com/Shoukox/ClientRenderer",
                    accessToken: null,
                    prerelease: false));

            var newVersion = await mgr.CheckForUpdatesAsync();
            if (newVersion == null)
                return;

            Logger.Log($"Found new update: {newVersion.TargetFullRelease.Version}");
            await mgr.DownloadUpdatesAsync(newVersion);
            Logger.Log("Update downloaded, restarting...");
            mgr.ApplyUpdatesAndRestart(newVersion, args);
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to check for updates: {ex.Message}");
        }
    }
}
