using ClientRenderer.Abstractions;
using ClientRenderer.Logging;
using ClientRenderer.Models;

namespace ClientRenderer.Startup;

public sealed class RenderWorker(IVideoRenderer videoRenderer, IServerConnection serverConnection, string chosenEncoder) : IRenderWorker
{
    private static async Task MonitorRenderCancellationAsync(int jobId, IServerConnection serverConnection, CancellationTokenSource renderCancellationTokenSource)
    {
        while (!renderCancellationTokenSource.IsCancellationRequested)
        {
            var renderJob = await serverConnection.GetRenderJobInfo(jobId);
            if (renderJob is { IsComplete: true, IsSuccess: false } &&
                string.Equals(renderJob.FailureReason, "Cancelled", StringComparison.OrdinalIgnoreCase))
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
                    break;

                Logger.Log($"[JobId:{renderJob.JobId}] New render job received!");

                info = new RenderPipelineInfo
                {
                    RenderJob = renderJob,
                    UseExperimentalRenderer = renderJob.RenderSettings.UseExperimentalRenderer,
                    ChosenRenderingEncoder = chosenEncoder
                };

                using var renderCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var renderCancellationMonitor = MonitorRenderCancellationAsync(renderJob.JobId, serverConnection, renderCancellationTokenSource);

                try
                {
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
                    continue;
                }

                throw;
            }
            catch (Exception e)
            {
                if (info?.RenderJob != null)
                {
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
        }
    }
}
