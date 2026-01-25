using ClientRenderer.Connection;
using ClientRenderer.Logging;
using ClientRenderer.Models;
using DanserWrapper;
using System.Collections.Concurrent;

namespace ClientRenderer.RenderPipeline
{
    public class ThumbnailRenderer
    {
        public async Task<bool> RenderThumbnail(RenderPipelineInfo info, ServerConnection serverConnection)
        {
            Logger.Log($"[JobId:{info.RenderJob!.JobId}] Generating a thumbnail...");
            DanserResult result;
            ConcurrentDictionary<string, string> renderUpdates = new();
            try
            {
                string arguments = $"-r \"{info.ReplayPath}\" " +
                                   $"-out \"{info.BeatmapHash}\" " +
                                   $"-ss \"{info.BeatmapLength + 6}\"";
                result = await new DanserGo().ExecuteAsync(arguments, new());
                if (!result.Success)
                {
                    arguments = $"-r \"{info.ReplayPath}\" " +
                                   $"-out \"{info.BeatmapHash}\" " +
                                   $"-ss \"{1}\"";
                    result = await new DanserGo().ExecuteAsync(arguments, new());
                }
                Logger.Log($"[JobId:{info.RenderJob!.JobId}] Successfully rendered a thumbnail!");

                Logger.Log($"[JobId:{info.RenderJob!.JobId}] Uploading the thumbnail...");
                await serverConnection.UploadThumbnail(Path.Combine(DanserGo.ScreenshotsPath, $"{info.BeatmapHash}.png"), info.RenderJob.JobId);
                Logger.Log($"[JobId:{info.RenderJob!.JobId}] The thumbnail was successfully uploaded!");
            }
            catch (Exception ex)
            {
                Logger.LogError($"[JobId:{info.RenderJob!.JobId}] Failed to render/upload a thumbnail! Skipping...");
                Logger.LogError(ex.ToString());
                return false;
            }

            return true;
        }
    }
}
