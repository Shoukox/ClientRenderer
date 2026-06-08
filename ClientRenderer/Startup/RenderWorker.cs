using ClientRenderer.Abstractions;
using ClientRenderer.Helpers;
using ClientRenderer.Logging;
using ClientRenderer.Models;

namespace ClientRenderer.Startup;

public sealed class RenderWorker(IVideoRenderer videoRenderer, IServerConnection serverConnection, string chosenEncoder) : IRenderWorker
{
    public event Action<bool>? RenderingStatus;
    private static async Task<bool> IsServerCancelledAsync(int jobId, IServerConnection serverConnection)
    {
        var renderJob = await serverConnection.GetRenderJobInfo(jobId);
        return renderJob is { IsComplete: true, IsSuccess: false } &&
               string.Equals(renderJob.FailureReason, "Cancelled", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task MonitorRenderCancellationAsync(int jobId, IServerConnection serverConnection, CancellationTokenSource renderCancellationTokenSource)
    {
        while (!renderCancellationTokenSource.IsCancellationRequested)
        {
            if (await IsServerCancelledAsync(jobId, serverConnection))
            {
                Logger.LogWarning($"[JobId:{jobId}] Server marked render as cancelled. Stopping local process...");
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
                Logger.Log("Waiting for new jobs...");
                var renderJob = await serverConnection.GetNextRenderJob();
                while (renderJob is null && !cancellationToken.IsCancellationRequested)
                {
                    Logger.LogError("Received a null render job, polling again in 5 seconds...");
                    await Task.Delay(5000, cancellationToken);
                    renderJob = await serverConnection.GetNextRenderJob();
                }

                if (renderJob is null)
                {
                    Logger.LogWarning($"Got a null render job...");
                    continue;
                }

                if (await IsServerCancelledAsync(renderJob.JobId, serverConnection))
                {
                    Logger.LogWarning($"[JobId:{renderJob.JobId}] Job was cancelled before rendering started. Skipping...");
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
                    Logger.LogWarning($"[JobId:{info.RenderJob.JobId}] Render was cancelled by the server.");
                    info = null;
                    continue;
                }

                throw;
            }
            catch (Exception e)
            {
                if (info?.RenderJob != null)
                {
                    if (await IsServerCancelledAsync(info.RenderJob.JobId, serverConnection))
                    {
                        Logger.LogWarning($"[JobId:{info.RenderJob.JobId}] Job was cancelled before rendering could proceed. Suppressing error.");
                        info = null;
                        continue;
                    }

                    try
                    {
                        await serverConnection.Failure(info.RenderJob.JobId, e.Message, false);
                    }
                    catch
                    {
                        // ignore secondary failures
                    }

                    Logger.LogError($"[JobId:{info.RenderJob.JobId}] Failed.");
                }

                Logger.LogError(e.ToString());
            }
            finally
            {
                info = null;
            }
        }
    }
}
