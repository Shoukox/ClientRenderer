using ClientRenderer.Abstractions;
using ClientRenderer.Logging;
using Velopack;
using Velopack.Sources;

namespace ClientRenderer.Startup;

public sealed class VelopackAutomaticUpdateService : IAutomaticUpdateService
{
    public async Task<bool> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            Logger.Log("Searching for updates...");

            UpdateManager manager = new UpdateManager(
                new GithubSource(
                    repoUrl: "https://github.com/Shoukox/ClientRenderer",
                    accessToken: null,
                    prerelease: false));

            if (!manager.IsInstalled)
            {
                Logger.Log("Skipping update check because the application was not installed with Velopack.");
                return false;
            }

            var updateInfo = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (updateInfo == null)
                return false;

            Logger.Log($"Found new update: {updateInfo.TargetFullRelease.Version}");
            await manager.DownloadUpdatesAsync(updateInfo, cancelToken: cancellationToken).ConfigureAwait(false);
            Logger.Log("Update downloaded, restarting...");

            manager.ApplyUpdatesAndRestart(updateInfo, Environment.GetCommandLineArgs().Skip(1).ToArray());
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            Logger.LogError($"Failed to check for updates: {ex.Message}");
            return false;
        }
    }
}
