using ClientRenderer.Abstractions;
using ClientRenderer.Helpers;
using ClientRenderer.Logging;
using ClientRenderer.Models;
using DanserWrapper;
using ExperimentalRendererWrapper;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClientRenderer.RenderPipeline
{
    public class VideoRenderer(
        IThumbnailRenderer thumbnailRenderer,
        IReplaysDownloader replaysDownloader,
        IBeatmapsetsDownloader beatmapsetsDownloader,
        ISkinsDownloader skinsDownloader) : IVideoRenderer
    {
        public async Task<bool> RenderVideo(RenderPipelineInfo info, IServerConnection serverConnection, CancellationToken cancellationToken)
        {
            if (!await replaysDownloader.DownloadReplay(info, serverConnection))
                return false;

            if (!await beatmapsetsDownloader.DownloadBeatmapset(info, serverConnection))
                return false;

            if (!await skinsDownloader.DownloadSkin(info, serverConnection))
                return false;

            if (info.UseExperimentalRenderer)
            {
                ExperimentalRenderer.AdjustConfig(info.RenderJob.RenderSettings.ToExperimentalRendererConfiguration());
            }
            else
            {
                DanserGo.AdjustConfig(info.RenderJob.RenderSettings.ToDanserConfiguration(info.HashedSkinName));
            }

            Logger.Log($"[JobId:{info.RenderJob!.JobId}] Start rendering");

            info.VideoPath = Path.Combine(DanserGo.VideosPath, $"{info.BeatmapHash}.mp4");
            var renderSuccess = !info.UseExperimentalRenderer
                ? await RenderWithDanser(info, serverConnection, cancellationToken)
                : await RenderWithExperimentalRenderer(info, serverConnection, cancellationToken);

            if (!renderSuccess)
                return false;

            Logger.Log($"[JobId:{info.RenderJob!.JobId}] Rendering done!");
            Logger.Log($"[JobId:{info.RenderJob!.JobId}] Uploading to the server...!");
            await serverConnection.PostVideo(info.VideoPath, info.RenderJob.JobId);

            Logger.Log($"[JobId:{info.RenderJob!.JobId}] Successfully uploaded");
            await thumbnailRenderer.RenderThumbnail(info, serverConnection, cancellationToken);
            await SetVideoDurationInSecondsAsync(info, cancellationToken);
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

        public async Task<bool> RenderWithDanser(RenderPipelineInfo info, IServerConnection serverConnection, CancellationToken cancellationToken)
        {
            DanserResult result;
            ConcurrentDictionary<string, string> renderUpdates = new();

            try
            {
                string[] arguments =
                [
                    "-r",
                    info.ReplayPath,
                    "-out",
                    Path.GetFileNameWithoutExtension(info.VideoPath),
                    "-preciseprogress"
                ];
                Task<DanserResult> renderTask = DanserGo.ExecuteAsync(arguments, renderUpdates, cancellationToken: cancellationToken);

                while (!renderTask.IsCompleted && !cancellationToken.IsCancellationRequested)
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
                Directory.CreateDirectory("logs");
                File.WriteAllText(Path.Combine("logs", $"danser_{DateTime.UtcNow:yyyyMMdd_HHmmss_ffff}.log"),
                    "Danser Standard Output:\n" + result.Output + "\n\n\nDanser Error Output:\n" + result.Error);
                return false;
            }

            return true;
        }

        public async Task<bool> RenderWithExperimentalRenderer(RenderPipelineInfo info, IServerConnection serverConnection, CancellationToken cancellationToken)
        {
            ExperimentalRendererResult result;
            ConcurrentDictionary<string, string> renderUpdates = new() { ["BeatmapLength"] = $"{info.BeatmapLength}" };
            try
            {
                var arguments = new List<string>
                {
                    "--yes",
                    "-ex",
                    "-pr",
                    "-R",
                    "--view",
                    "file",
                    info.ReplayPath,
                    "-osz",
                    info.BeatmapsetOszPath,
                    "--config",
                    ExperimentalRenderer.ConfigPath,
                    "-O",
                    info.VideoPath
                };

                if (info.RenderJob.RenderSettings.SkinName != "default")
                {
                    arguments.Add("--skin");
                    arguments.Add("import");
                    arguments.Add(info.SkinOskPath);
                }

                if (info.RenderJob.RenderSettings.ShowPP)
                {
                    arguments.Add("-exp");
                    arguments.Add("pp-counter");
                }

                Logger.Log($"[JobId:{info.RenderJob!.JobId}] Experimental renderer args: {string.Join(' ', arguments)}");

                var renderTask = ExperimentalRenderer.ExecuteAsync(arguments, renderUpdates, cancellationToken: cancellationToken);

                while (!renderTask.IsCompleted && !cancellationToken.IsCancellationRequested)
                {
                    if (renderUpdates.TryGetValue("Progress", out string? progressString) &&
                        double.TryParse(progressString, out double progress) && progress != 0)
                    {
                        try
                        {
                            await serverConnection.ReportRenderingProgress(info.RenderJob!.JobId, Math.Min(1.0, progress));
                            Logger.Log($"[JobId:{info.RenderJob!.JobId}] Rendering progress: {progress * 100:0.00}%");
                        }
                        catch
                        {
                            continue;
                        }
                    }
                    await Task.Delay(1000, cancellationToken);
                }

                result = await renderTask;
            }
            catch (OperationCanceledException)
            {
                Logger.LogWarning($"[JobId:{info.RenderJob!.JobId}] Experimental renderer was cancelled.");
                throw;
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
                Logger.LogError($"[JobId:{info.RenderJob!.JobId}] Failed to render replay! Saving danser logs");
                Directory.CreateDirectory("logs");
                File.WriteAllText(Path.Combine("logs", $"experimental-renderer_{DateTime.UtcNow:yyyyMMdd_HHmmss_ffff}.log"),
                    "Experimental Renderer Standard Output:\n" + result.Output + "\n\n\nExperimental Renderer Error Output:\n" + result.Error);
                return false;
            }

            return true;
        }

        public async Task SetVideoDurationInSecondsAsync(RenderPipelineInfo info, CancellationToken cancellationToken, int timeoutMs = 10_000)
        {
            string FfprobePath = Path.Combine(Path.GetDirectoryName(ExperimentalRenderer.FfmpegPath)!, "ffprobe.exe");
            var psi = new ProcessStartInfo
            {
                FileName = FfprobePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            psi.ArgumentList.Add("-v");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-show_entries");
            psi.ArgumentList.Add("format=duration");
            psi.ArgumentList.Add("-of");
            psi.ArgumentList.Add("json");
            psi.ArgumentList.Add(info.VideoPath);

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Could not start ffprobe.");

            string output = await process.StandardOutput.ReadToEndAsync();
            string error = await process.StandardError.ReadToEndAsync();

            using var timeoutCts = new CancellationTokenSource(timeoutMs);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            try
            {
                await process.WaitForExitAsync(linkedCts.Token);

                if (process.ExitCode != 0)
                {
                    Logger.Log($"[JobId:{info.RenderJob!.JobId}] Failed to calculate a video duration! Skipping...");
                    return;
                }

                using var doc = JsonDocument.Parse(output);
                string durationString = doc.RootElement
                    .GetProperty("format")
                    .GetProperty("duration")
                    .GetString()
                    ?? throw new InvalidOperationException("Could not read duration.");

                double seconds = double.Parse(durationString, CultureInfo.InvariantCulture);
                Logger.Log($"[JobId:{info.RenderJob!.JobId}] Video duration: {seconds} seconds.");
                info.RenderJob.VideoDuration = (int)seconds;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (!process.HasExited)
                    process.Kill(true);

                Logger.LogError($"[JobId:{info.RenderJob!.JobId}] Failed to calculate a video duration. Cancelled.");
                throw;
            }
            catch (Exception ex)
            {
                if (!process.HasExited)
                    process.Kill(true);

                Logger.LogError($"[JobId:{info.RenderJob!.JobId}] Failed to calculate a video duration! Skipping...");
                Logger.LogError(ex.ToString());
                return;
            }
        }
    }
}
