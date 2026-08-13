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
            Logger.Log($"[JobId:{info.RenderJob!.JobId}] Generating a thumbnail with '{FfmpegPath}'...");
            string thumbnailPath = Path.Combine(DanserGo.ScreenshotsPath, $"{info.BeatmapHash}.jpg");
            ProcessStartInfo processStartInfo = new ProcessStartInfo
            {
                FileName = FfmpegPath,
                ArgumentList = { "-y", "-sseof", "-1", "-i", info.VideoPath, "-frames:v", "1", "-vf", "scale='min(1280,iw)':-2", "-q:v", "15", thumbnailPath },
                WorkingDirectory = ExperimentalRenderer.ExperimentalRendererDirectoryPath,

                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using Process process = new Process { StartInfo = processStartInfo };
            bool processStarted = false;

            try
            {
                process.Start();
                processStarted = true;

                using CancellationTokenSource timeoutCts = new CancellationTokenSource(timeoutMs);
                using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
                await process.WaitForExitAsync(linkedCts.Token);
                bool success = process.ExitCode == 0;
                if (success)
                {
                    Logger.Log($"[JobId:{info.RenderJob!.JobId}] Thumbnail rendered successfully.");
                }
                else
                {
                    Logger.LogError($"[JobId:{info.RenderJob!.JobId}] Failed to render thumbnail. ffmpeg exit code: {process.ExitCode}.");
                    return false;
                }

                Logger.Log($"[JobId:{info.RenderJob!.JobId}] Uploading thumbnail...");
                await serverConnection.UploadThumbnail(thumbnailPath, info.RenderJob.JobId);
                Logger.Log($"[JobId:{info.RenderJob!.JobId}] Thumbnail uploaded successfully.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (processStarted && !process.HasExited)
                    process.Kill(true);

                Logger.LogError($"[JobId:{info.RenderJob!.JobId}] Failed to render or upload thumbnail. Operation was canceled.");
                throw;
            }
            catch (Exception ex)
            {
                if (processStarted && !process.HasExited)
                    process.Kill(true);

                Logger.LogError(ex, $"[JobId:{info.RenderJob!.JobId}] Failed to render or upload thumbnail. Skipping thumbnail.");
                return false;
            }

            return true;
        }
    }
}
