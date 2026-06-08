using ClientRenderer.GUI.Services.Localization;
using ClientRenderer.Logging;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace ClientRenderer.GUI.Services
{
    public enum UpdateCheckResult
    {
        Busy,
        NoUpdates,
        UpdateApplied,
        SkippedNotInstalled,
        Failed
    }

    public sealed class UpdateService
    {
        public static UpdateService Instance { get; } = new();

        private readonly LocalizationService _localizer = App.Localizer;
        private readonly SemaphoreSlim _checkGate = new(1, 1);
        private int _isChecking;

        private UpdateService()
        {
        }

        public bool IsCheckingForUpdates => Volatile.Read(ref _isChecking) == 1;

        public event Action<bool>? CheckingStateChanged;

        public async Task<UpdateCheckResult> CheckForUpdatesAsync(bool silentIfUpToDate, string[]? restartArgs = null, CancellationToken cancellationToken = default)
        {
            if (!await _checkGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
                return UpdateCheckResult.Busy;

            setCheckingState(true);

            try
            {
                Logger.Log(_localizer["Updates.Checking"]);

                UpdateManager manager = new UpdateManager(
                    new GithubSource(
                        repoUrl: "https://github.com/Shoukox/ClientRenderer",
                        accessToken: null,
                        prerelease: false));

                if (!manager.IsInstalled)
                {
                    Logger.Log(_localizer["Updates.SkipNotInstalled"]);
                    return UpdateCheckResult.SkippedNotInstalled;
                }

                var updateInfo = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
                if (updateInfo == null)
                {
                    if (!silentIfUpToDate)
                        Logger.Log(_localizer["Updates.NoUpdates"]);
                    return UpdateCheckResult.NoUpdates;
                }

                Logger.Log(string.Format(_localizer["Updates.Found"], updateInfo.TargetFullRelease.Version));
                await manager.DownloadUpdatesAsync(updateInfo, cancelToken: cancellationToken).ConfigureAwait(false);
                Logger.Log(_localizer["Updates.DownloadedRestarting"]);

                var args = restartArgs ?? Environment.GetCommandLineArgs().Skip(1).ToArray();
                manager.ApplyUpdatesAndRestart(updateInfo, args);
                return UpdateCheckResult.UpdateApplied;
            }
            catch (OperationCanceledException)
            {
                return UpdateCheckResult.Failed;
            }
            catch (Exception ex)
            {
                Logger.LogError(string.Format(_localizer["Updates.Failed"], ex.Message));
                return UpdateCheckResult.Failed;
            }
            finally
            {
                setCheckingState(false);
                _checkGate.Release();
            }
        }

        private void setCheckingState(bool isChecking)
        {
            Interlocked.Exchange(ref _isChecking, isChecking ? 1 : 0);
            CheckingStateChanged?.Invoke(isChecking);
        }
    }
}
