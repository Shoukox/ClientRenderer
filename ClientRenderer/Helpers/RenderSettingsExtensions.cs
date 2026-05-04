using ClientRenderer.Models;
using DanserWrapper;
using ExperimentalRendererWrapper;
using ExperimentalRendererWrapper.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ClientRenderer.Helpers
{
    public static class RenderSettingsExtensions
    {
        public static DanserConfiguration ToDanserConfiguration(this RenderSettings renderSettings, string hashedSkinName)
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
                CursorSize = renderSettings.CursorSize,
            };
        }

        public static ExperimentalRendererConfiguration ToExperimentalRendererConfiguration(this RenderSettings renderSettings)
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
                    LibrariesPath = Path.GetDirectoryName(ExperimentalRenderer.FfmpegPath),
                    Executable = ExperimentalRenderer.FfmpegPath,
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
                    ManiaScrollDirectionUp = renderSettings.ManiaScrollDirectionUp ? "up" : "down",
                    CursorSize = renderSettings.CursorSize,
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
    }
}
