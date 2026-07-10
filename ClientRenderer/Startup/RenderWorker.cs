using ClientRenderer.Abstractions;
using ClientRenderer.Helpers;
using ClientRenderer.Logging;
using ClientRenderer.Models;

namespace ClientRenderer.Startup;

public sealed class RenderWorker(IVideoRenderer videoRenderer, IServerConnection serverConnection, string chosenEncoder, IAutomaticUpdateService? automaticUpdateService = null) : IRenderWorker
{
    public event Action<bool>? RenderingStatus;
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(1);
    private DateTimeOffset _nextUpdateCheckAt = DateTimeOffset.UtcNow + UpdateCheckInterval;

    private static async Task<bool> IsServerCanceledAsync(int jobId, IServerConnection serverConnection)
    {
        var renderJob = await serverConnection.GetRenderJobInfo(jobId);
        return renderJob is { IsComplete: true, IsSuccess: false } &&
               string.Equals(renderJob.FailureReason, "Cancelled", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task MonitorRenderCancellationAsync(int jobId, IServerConnection serverConnection, CancellationTokenSource renderCancellationTokenSource)
    {
        while (!renderCancellationTokenSource.IsCancellationRequested)
        {
            if (await IsServerCanceledAsync(jobId, serverConnection))
            {
                Logger.LogWarning($"[JobId:{jobId}] Server marked render as canceled. Stopping local process...");
                renderCancellationTokenSource.Cancel();
                return;
            }

            await Task.Delay(2000, renderCancellationTokenSource.Token);
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        RenderPipelineInfo? info = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await CheckForUpdatesIfDueAsync(cancellationToken);

                Logger.Log("Waiting for new jobs...");
                var renderJob = await serverConnection.GetNextRenderJob();
                while (renderJob is null && !cancellationToken.IsCancellationRequested)
                {
                    Logger.LogWarning("No render job was available. Polling again in 5 seconds...");
                    await Task.Delay(5000, cancellationToken);
                    await CheckForUpdatesIfDueAsync(cancellationToken);
                    renderJob = await serverConnection.GetNextRenderJob();
                }

                if (renderJob is null)
                {
                    Logger.LogWarning("Render job polling returned no job.");
                    continue;
                }

                if (await IsServerCanceledAsync(renderJob.JobId, serverConnection))
                {
                    Logger.LogWarning($"[JobId:{renderJob.JobId}] Job was canceled before rendering started. Skipping...");
                    continue;
                }

                Logger.Log($"[JobId:{renderJob.JobId}] New render job received!");

                info = new RenderPipelineInfo
                {
                    RenderJob = renderJob,
                    UseExperimentalRenderer = renderJob.RenderSettings.UseExperimentalRenderer,
                    ChosenRenderingEncoder = chosenEncoder
                };

                using CancellationTokenSource renderCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var renderCancellationMonitor = MonitorRenderCancellationAsync(renderJob.JobId, serverConnection, renderCancellationTokenSource);

                RenderingStatus?.Invoke(true);
                try
                {
                    WindowsSleepPreventerHelper.PreventSleepAndDisplayOff();
                    await videoRenderer.RenderVideo(info, serverConnection, renderCancellationTokenSource.Token);
                }
                finally
                {
                    await renderCancellationTokenSource.CancelAsync();
                    try
                    {
                        await renderCancellationMonitor;
                    }
                    catch (OperationCanceledException)
                    {
                        Logger.Log($"[JobId:{renderJob.JobId}] Render cancellation monitor stopped.");
                    }

                    WindowsSleepPreventerHelper.AllowSleep();
                    RenderingStatus?.Invoke(false);
                }
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    Logger.LogWarning("Render worker cancellation requested.");
                    break;
                }

                if (info?.RenderJob != null)
                {
                    Logger.LogWarning($"[JobId:{info.RenderJob.JobId}] Render was canceled by the server.");
                    info = null;
                    continue;
                }

                throw;
            }
            catch (Exception e)
            {
                if (info?.RenderJob != null)
                {
                    if (await IsServerCanceledAsync(info.RenderJob.JobId, serverConnection))
                    {
                        Logger.LogWarning($"[JobId:{info.RenderJob.JobId}] Job was canceled before rendering could proceed. Suppressing error.");
                        info = null;
                        continue;
                    }

                    try
                    {
                        await serverConnection.Failure(info.RenderJob.JobId, e.Message, false);
                    }
                    catch (Exception failureReportException)
                    {
                        Logger.LogError(failureReportException, $"[JobId:{info.RenderJob.JobId}] Failed to report render failure to the server.");
                    }

                    Logger.LogError($"[JobId:{info.RenderJob.JobId}] Render failed.");
                }

                Logger.LogError(e, "Unexpected render worker error.");
            }
            finally
            {
                info = null;
            }
        }
    }

    private async Task CheckForUpdatesIfDueAsync(CancellationToken cancellationToken)
    {
        if (automaticUpdateService == null || DateTimeOffset.UtcNow < _nextUpdateCheckAt)
            return;

        _nextUpdateCheckAt = DateTimeOffset.UtcNow + UpdateCheckInterval;
        Logger.Log("Checking for updates before polling for another render job.");
        await automaticUpdateService.CheckForUpdatesAsync(cancellationToken);
    }
}
