using ClientRenderer.Abstractions;
using ClientRenderer.Logging;
using ClientRenderer.Models;
using DanserWrapper;
using ExperimentalRendererWrapper;
using System.Diagnostics;

namespace ClientRenderer.RenderPipeline
{
    public class ThumbnailRenderer : IThumbnailRenderer
    {
        private string FfmpegPath => ExperimentalRenderer.FfmpegPath;
        public async Task<bool> RenderThumbnail(RenderPipelineInfo info, IServerConnection serverConnection, CancellationToken cancellationToken, int timeoutMs = 10_000)
        {
            Logger.Log($"[JobId:{info.RenderJob!.JobId}] Generating a thumbnail...");
            string thumbnailPath = Path.Combine(DanserGo.ScreenshotsPath, $"{info.BeatmapHash}.jpg");
            ProcessStartInfo processStartInfo = new ProcessStartInfo
            {
                FileName = FfmpegPath,
                ArgumentList = { "-y", "-sseof", "-1", "-i", info.VideoPath, "-frames:v", "1", "-vf", "scale='min(1280,iw)':-2", "-q:v", "15", thumbnailPath },
                WorkingDirectory = Path.GetDirectoryName(AppContext.BaseDirectory),

                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using Process process = new Process { StartInfo = processStartInfo };
            process.Start();
            using CancellationTokenSource timeoutCts = new CancellationTokenSource(timeoutMs);
            using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(linkedCts.Token);
                bool success = process.ExitCode == 0;
                if (success)
                {
                    Logger.Log($"[JobId:{info.RenderJob!.JobId}] Successfully rendered a thumbnail!");
                }
                else
                {
                    Logger.Log($"[JobId:{info.RenderJob!.JobId}] Failed to render a thumbnail!");
                    return false;
                }

                Logger.Log($"[JobId:{info.RenderJob!.JobId}] Uploading the thumbnail...");
                await serverConnection.UploadThumbnail(thumbnailPath, info.RenderJob.JobId);
                Logger.Log($"[JobId:{info.RenderJob!.JobId}] The thumbnail was successfully uploaded!");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (!process.HasExited)
                    process.Kill(true);

                Logger.LogError($"[JobId:{info.RenderJob!.JobId}] Failed to render/upload a thumbnail! Cancelled.");
                throw;
            }
            catch (Exception ex)
            {
                if (!process.HasExited)
                    process.Kill(true);

                Logger.LogError($"[JobId:{info.RenderJob!.JobId}] Failed to render/upload a thumbnail! Skipping...");
                Logger.LogError(ex.ToString());
                return false;
            }

            return true;
        }
    }
}
