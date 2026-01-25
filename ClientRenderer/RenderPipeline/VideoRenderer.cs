using ClientRenderer.Connection;
using ClientRenderer.Logging;
using ClientRenderer.Models;
using ClientRenderer.Render;
using DanserWrapper;
using ExperimentalRendererWrapper;
using OsuApi.BanchoV2;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace ClientRenderer.RenderPipeline
{
    public class VideoRenderer(BanchoApiV2 banchoApiV2, string osuSessionCookie)
    {
        private ThumbnailRenderer _thumbnailRenderer = new();
        private ReplaysDownloader _replaysDownloader = new();
        private BeatmapsetsDownloader _beatmapsetsDownloader = new(banchoApiV2, osuSessionCookie);
        private SkinsDownloader _skinsDownloader = new();
        public async Task<bool> RenderVideo(RenderPipelineInfo info, ServerConnection serverConnection, CancellationToken cancellationToken)
        {
            // Download replay
            if (!await _replaysDownloader.DownloadReplay(info, serverConnection))
            {
                return false;
            }

            // Download beatmap
            if (!await _beatmapsetsDownloader.DownloadBeatmapset(info, serverConnection, osuSessionCookie))
            {
                return false;
            }

            // Download skin if needed
            if (!await _skinsDownloader.DownloadSkin(info, serverConnection))
            {
                return false;
            }
            DanserGo.AdjustConfig(info.RenderJob.RenderSettings);
            Logger.Log($"[JobId:{info.RenderJob!.JobId}] Start rendering");

            // Render using danser-go
            info.VideoPath = Path.Combine(DanserGo.VideosPath, $"{info.BeatmapHash}{info.FileTimeNow}.mp4");
            if (!info.UseExperimentalRenderer)
            {
                await RenderWithDanser(info, serverConnection, cancellationToken);
            }

            // Render using experimental renderer
            else
            {
                await RenderWithExperimentalRenderer(info, serverConnection, cancellationToken);
            }

            Logger.Log($"[JobId:{info.RenderJob!.JobId}] Rendering done!");
            Logger.Log($"[JobId:{info.RenderJob!.JobId}] Uploading to the server...!");
            await serverConnection.PostVideo(info.VideoPath, info.RenderJob.JobId);

            Logger.Log($"[JobId:{info.RenderJob!.JobId}] Successfully uploaded");
            if (info.DecodedReplay.Ruleset is OsuParsers.Enums.Ruleset.Standard)
            {
                await _thumbnailRenderer.RenderThumbnail(info, serverConnection);
            }
            else
            {
                Logger.Log("A thumbnail will not be rendered - the replay is not from osu!std");
            }

            try
            {
                await serverConnection.SetRenderJobMetadata(info.RenderJob);
            }
            catch (Exception ex)
            {
                Logger.LogError($"[JobId:{info.RenderJob!.JobId}] Failed to set render job metadata! Skipping...");
                Logger.LogError(ex.ToString());
            }

            await serverConnection.FinishRendering(info.RenderJob.JobId);
            Logger.Log($"[JobId:{info.RenderJob!.JobId}] Rendering finished");

            return true;
        }

        public async Task<bool> RenderWithDanser(RenderPipelineInfo info, ServerConnection serverConnection, CancellationToken cancellationToken)
        {
            DanserResult result;
            ConcurrentDictionary<string, string> renderUpdates = new();
            try
            {
                string arguments = $"-r \"{info.ReplayPath}\" " +
                                  $"-out \"{Path.GetFileNameWithoutExtension(info.VideoPath)}\" " +
                                  $"-preciseprogress";
                Task<DanserResult> renderTask = new DanserGo().ExecuteAsync(arguments, renderUpdates);

                while (renderTask.IsCompleted == false && !cancellationToken.IsCancellationRequested)
                {
                    if (renderUpdates.TryGetValue("Progress", out string? progressString) &&
                        double.TryParse(progressString, out double progress) && progress != 0)
                    {
                        await serverConnection.ReportRenderingProgress(info.RenderJob!.JobId, Math.Min(1.0, progress));
                        Logger.Log($"[JobId:{info.RenderJob!.JobId}] Rendering progress: {progress * 100:0.00}%");
                    }
                    await Task.Delay(1000, cancellationToken);
                }

                result = await renderTask;

                // Match map name
                var mapNameRegex = new Regex(@"Playing: (.*)", RegexOptions.Compiled);
                var matchMapName = mapNameRegex.Match(result.Output + "\n" + result.Error);
                if (matchMapName.Success && !renderUpdates.ContainsKey("Map"))
                {
                    info.RenderJob.MapName = matchMapName.Groups[1].Value.Trim();
                }
            }
            catch (Exception ex)
            {
                await serverConnection.Failure(info.RenderJob.JobId, "danser", false);
                Logger.LogError($"[JobId:{info.RenderJob!.JobId}] Failed to render a replay! Error when calling danser-go");
                Logger.LogError(ex.ToString());
                return false;
            }

            if (!result.Success)
            {
                await serverConnection.Failure(info.RenderJob.JobId, "danser", false);
                Logger.LogError($"[JobId:{info.RenderJob!.JobId}] Failed to render a replay! Saving danser logs");
                File.WriteAllText(Path.Combine($"danser_Logger.Log{DateTime.UtcNow.ToFileTimeUtc()}"), "Danser Standard Output:\n" + result.Output + "\n\n\nDanser Error Output:\n" + result.Error);
                return false;
            }

            return true;
        }

        public async Task<bool> RenderWithExperimentalRenderer(RenderPipelineInfo info, ServerConnection serverConnection, CancellationToken cancellationToken)
        {
            ExperimentalRendererResult result;
            ConcurrentDictionary<string, string> renderUpdates = new() { ["BeatmapLength"] = $"{info.BeatmapLength}" };
            try
            {
                string arguments =
                    $"--view file \"{info.ReplayPath}\" " +
                    $"--import-beatmap \"{info.BeatmapsetOszPath}\" " +
                    $"--record " +
                    $"--record-output \"{info.VideoPath}\" " +
                    $"--yes ";

                if (info.RenderJob.RenderSettings.SkinName != "default")
                {
                    arguments += $"--skin import \"{info.SkinOskPath}\"";
                }

                var renderTask = new ExperimentalRenderer().ExecuteAsync(arguments, renderUpdates);

                while (renderTask.IsCompleted == false && !cancellationToken.IsCancellationRequested)
                {
                    if (renderUpdates.TryGetValue("Progress", out string? progressString) &&
                        double.TryParse(progressString, out double progress) && progress != 0)
                    {
                        await serverConnection.ReportRenderingProgress(info.RenderJob!.JobId, Math.Min(1.0, progress));
                        Logger.Log($"[JobId:{info.RenderJob!.JobId}] Rendering progress: {progress * 100:0.00}%");
                    }
                    await Task.Delay(1000, cancellationToken);
                }

                result = await renderTask;
            }
            catch (Exception ex)
            {
                await serverConnection.Failure(info.RenderJob.JobId, "Failed to render a replay using experimental renderer", false);
                Logger.LogError($"[JobId:{info.RenderJob!.JobId}] Failed to render replay! Error when calling experimental renderer");
                Logger.LogError(ex.ToString());
                return false;
            }

            if (!result.Success)
            {
                await serverConnection.Failure(info.RenderJob.JobId, "Failed to render a replay using experimental renderer. Result is not successful", false);
                Logger.LogError($"[JobId:{info.RenderJob!.JobId}] Failed to render replay! Saving danser Logger.Logs");
                File.WriteAllText(Path.Combine($"experimental-renderer_Logger.Log{DateTime.UtcNow.ToFileTimeUtc()}"), "Experimental Renderer Standard Output:\n" + result.Output + "\n\n\nExperimental Renderer Error Output:\n" + result.Error);
                return false;
            }

            return true;
        }
    }
}
