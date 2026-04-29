using ClientRenderer.Abstractions;
using ClientRenderer.Logging;
using ClientRenderer.Models;
using DanserWrapper;
using ExperimentalRendererWrapper;
using ExperimentalRendererWrapper.Configuration;
using System.Collections.Concurrent;
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
                ExperimentalRenderer.AdjustConfig(ToExperimentalRendererConfiguration(info.RenderJob.RenderSettings));
            }
            else
            {
                DanserGo.AdjustConfig(ToDanserConfiguration(info.RenderJob.RenderSettings, info.HashedSkinName));
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
            if (info.DecodedReplay.Ruleset is OsuParsers.Enums.Ruleset.Standard)
            {
                await thumbnailRenderer.RenderThumbnail(info, serverConnection);
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

        private static DanserConfiguration ToDanserConfiguration(RenderSettings renderSettings, string hashedSkinName)
        {
            return new DanserConfiguration
            {
                VideoWidth = renderSettings.VideoWidth,
                VideoHeight = renderSettings.VideoHeight,
                Encoder = renderSettings.Encoder,
                SkinName = hashedSkinName, // because of the way we are saving our skins in danser
                GeneralVolume = renderSettings.GeneralVolume,
                MusicVolume = renderSettings.MusicVolume,
                SampleVolume = renderSettings.SampleVolume,
                BackgroundDim = renderSettings.BackgroundDim,
                HitErrorMeter = renderSettings.HitErrorMeter,
                AimErrorMeter = renderSettings.AimErrorMeter,
                HPBar = renderSettings.HPBar,
                ShowPP = renderSettings.ShowPP,
                HitCounter = renderSettings.HitCounter,
                IgnoreFailsInReplays = renderSettings.IgnoreFailsInReplays,
                Video = renderSettings.Video,
                Storyboard = renderSettings.Storyboard,
                Mods = renderSettings.Mods,
                KeyOverlay = renderSettings.KeyOverlay,
                Combo = renderSettings.Combo,
                Leaderboard = renderSettings.Leaderboard,
                StrainGraph = renderSettings.StrainGraph,
                MotionBlur = renderSettings.MotionBlur,
            };
        }

        private static ExperimentalRendererConfiguration ToExperimentalRendererConfiguration(RenderSettings renderSettings)
        {
            return new ExperimentalRendererConfiguration
            {
                RecordOptions = new RecordOptionsObject
                {
                    FrameRate = 60,
                    Resolution = $"{renderSettings.VideoWidth}x{renderSettings.VideoHeight}",
                    Renderer = "Legacy"
                },
                FFmpegOptions = new FFmpegOptionsObject
                {
                    Mode = "Pipe",
                    LibrariesPath = "ffmpeg",
                    Executable = "ffmpeg",
                    VideoEncoder = renderSettings.Encoder,
                    VideoEncoderPreset = MapExperimentalEncoderPreset(renderSettings.Encoder),
                    VideoEncoderBitrate = "5M"
                },
                OutputOptions = new OutputOptionsObject
                {
                    PixelFormat = "RGB"
                },
                GameSettings = new GameSettings
                {
                    SkipIntro = false,
                    BackgroundDim = renderSettings.BackgroundDim,
                    ShowStoryboard = renderSettings.Storyboard || renderSettings.Video,
                    BeatmapHitsounds = false,
                    BeatmapSkin = false,
                    BeatmapColors = false,
                    VolumeMusic = renderSettings.MusicVolume,
                    VolumeEffects = renderSettings.SampleVolume,
                    VolumeMaster = renderSettings.GeneralVolume,
                    ManiaScrollSpeed = renderSettings.ManiaScrollSpeed,
                    ManiaScrollDirectionUp = renderSettings.ManiaScrollDirectionUp ? "up" : "down"
                }
            };
        }

        private static string MapExperimentalEncoderPreset(string encoder)
        {
            return encoder switch
            {
                "h264_nvenc" => "p1",
                "av1_nvenc" => "p1",
                "libx264" => "veryfast",
                _ => "fast"
            };
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
                Task<DanserResult> renderTask = DanserGo.ExecuteAsync(arguments, renderUpdates);

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

                var renderTask = ExperimentalRenderer.ExecuteAsync(arguments, renderUpdates);

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
    }
}
