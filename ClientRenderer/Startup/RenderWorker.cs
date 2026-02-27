using ClientRenderer.Abstractions;
using ClientRenderer.Logging;
using ClientRenderer.Models;

namespace ClientRenderer.Startup;

public sealed class RenderWorker(IVideoRenderer videoRenderer, IServerConnection serverConnection, string chosenEncoder) : IRenderWorker
{
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

                await videoRenderer.RenderVideo(info, serverConnection, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Logger.LogWarning("Render worker cancellation requested.");
                break;
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
