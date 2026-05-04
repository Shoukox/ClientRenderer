using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace DanserWrapper;

public static class DanserGo
{
    public static string DanserGoPath = Path.Combine(AppContext.BaseDirectory, "danser", "danser-cli");
    public readonly static string DanserGoDirectoryPath = Path.GetDirectoryName(DanserGoPath)!;
    public readonly static string VideosPath = Path.Combine(DanserGoDirectoryPath, "videos");
    public readonly static string ScreenshotsPath = Path.Combine(DanserGoDirectoryPath, "screenshots");
    public readonly static string SongsPath = Path.Combine(DanserGoDirectoryPath, "songs");

    public static async Task<DanserResult> ExecuteAsync(IEnumerable<string> args, ConcurrentDictionary<string, string> renderUpdates, int timeoutMs = 1000_000, CancellationToken cancellationToken = default)
    {
        if (!DanserExists())
        {
            throw new FileNotFoundException($"danser-go executable was not found at: {DanserGoPath}");
        }
        CreateDirectoriesIfNeeded();

        var processStartInfo = new ProcessStartInfo
        {
            FileName = DanserGoPath,
            UseShellExecute = false,
            CreateNoWindow = true,

            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,

            WorkingDirectory = Path.GetDirectoryName(DanserGoPath)
        };
        foreach (string arg in args)
        {
            processStartInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = processStartInfo };

        var outputStringBuilder = new StringBuilder();
        var errorStringBuilder = new StringBuilder();

        var progressRegex = new Regex(@"Progress: (\d+)%", RegexOptions.Compiled);
        process.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data))
                return;
            outputStringBuilder.AppendLine(e.Data);

            // Match progress
            var matchProgress = progressRegex.Match(e.Data);
            if (matchProgress.Success)
            {
                var progress = double.Parse(matchProgress.Groups[1].Value);
                renderUpdates["Progress"] = $"{progress / 100}";
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data))
                return;
            errorStringBuilder.AppendLine(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(timeoutMs);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited)
                process.Kill(true);

            throw;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(true);

            throw new TimeoutException($"danser-go process timed out after {timeoutMs}ms");
        }

        string outputText = outputStringBuilder.ToString();
        string errorText = errorStringBuilder.ToString();
        bool success = process.ExitCode == 0 && outputText.Contains("Exiting normally.");
        return new DanserResult
        {
            ExitCode = process.ExitCode,
            Output = outputText,
            Error = errorText,
            Success = success
        };
    }

    /// <summary>
    /// Edits some values in danser config file
    /// </summary>
    /// <param name="encoder">Use h264_nvenc for nvidia and libx264 for other</param>
    public static void AdjustConfig(DanserConfiguration configuration)
    {
        string configPath = Path.Combine(DanserGoDirectoryPath, "settings", "default.json");
        var json = JsonConvert.DeserializeObject<dynamic>(File.ReadAllText(configPath))!;

        json["General"]["OsuSongsDir"] = SongsPath;
        json["General"]["OsuSkinsDir"] = Path.Combine(DanserGoDirectoryPath, "skins");
        json["General"]["OsuReplaysDir"] = Path.Combine(DanserGoDirectoryPath, "replays");

        json["Audio"]["GeneralVolume"] = configuration.GeneralVolume;
        json["Audio"]["MusicVolume"] = configuration.MusicVolume;
        json["Audio"]["SampleVolume"] = configuration.SampleVolume;

        json["Recording"]["Encoder"] = configuration.Encoder;
        json["Recording"]["AudioCodec"] = "aac";
        json["Recording"]["FrameWidth"] = configuration.VideoWidth;
        json["Recording"]["FrameHeight"] = configuration.VideoHeight;
        json["Recording"]["FPS"] = 60;
        json["Recording"]["OutputDir"] = "videos";
        json["Recording"]["libx264"]["RateControl"] = "crf";
        json["Recording"]["libx264"]["Bitrate"] = "5M";
        json["Recording"]["libx264"]["CRF"] = 30;
        json["Recording"]["libx264"]["Profile"] = "high";
        json["Recording"]["libx264"]["Preset"] = "veryfast";
        json["Recording"]["h264_nvenc"]["RateControl"] = "cq";
        json["Recording"]["h264_nvenc"]["Bitrate"] = "5M";
        json["Recording"]["h264_nvenc"]["VBR"] = 30;
        json["Recording"]["h264_nvenc"]["Profile"] = "main";
        json["Recording"]["h264_nvenc"]["Preset"] = "p1";
        json["Recording"]["av1_nvenc"]["RateControl"] = "cbr";
        json["Recording"]["av1_nvenc"]["Bitrate"] = "5M";
        json["Recording"]["av1_nvenc"]["VBR"] = 30;
        json["Recording"]["av1_nvenc"]["Preset"] = "p1";

        json["Recording"]["MotionBlur"]["Enabled"] = configuration.MotionBlur;

        json["Skin"]["CurrentSkin"] = configuration.SkinName;
        json["Skin"]["FallbackSkin"] = "default";
        json["Skin"]["Cursor"]["Scale"] = configuration.CursorSize;

        json["Objects"]["Sliders"]["Snaking"]["In"] = false;
        json["Objects"]["Sliders"]["Snaking"]["Out"] = false;

        json["Gameplay"]["IgnoreFailsInReplays"] = configuration.IgnoreFailsInReplays;
        json["Gameplay"]["HitErrorMeter"]["Show"] = configuration.HitErrorMeter;
        json["Gameplay"]["AimErrorMeter"]["Show"] = configuration.AimErrorMeter;
        json["Gameplay"]["HpBar"]["Show"] = configuration.HPBar;
        json["Gameplay"]["PPCounter"]["Show"] = configuration.ShowPP;
        json["Gameplay"]["PPCounter"]["ShowInResults"] = false; // for fetching the result screen 
        json["Gameplay"]["HitCounter"]["Show"] = configuration.HitCounter;
        json["Gameplay"]["KeyOverlay"]["Show"] = configuration.KeyOverlay;
        json["Gameplay"]["Mods"]["Show"] = configuration.Mods;
        json["Gameplay"]["ComboCounter"]["Show"] = configuration.Combo;
        json["Gameplay"]["ScoreBoard"]["Show"] = configuration.Leaderboard;
        json["Gameplay"]["ScoreBoard"]["ModsOnly"] = false;
        json["Gameplay"]["StrainGraph"]["Show"] = configuration.StrainGraph;
        json["Gameplay"]["StrainGraph"]["Outline"]["Show"] = true;

        json["Playfield"]["Background"]["LoadStoryboards"] = configuration.Video;
        json["Playfield"]["Background"]["LoadVideos"] = configuration.Storyboard;
        json["Playfield"]["Background"]["Dim"]["Intro"] = 0;
        json["Playfield"]["Background"]["Dim"]["Normal"] = configuration.BackgroundDim;
        json["Playfield"]["Background"]["Dim"]["Breaks"] = configuration.BackgroundDim * 0.8;
        json["Playfield"]["SeizureWarning"]["Enabled"] = false;

        File.WriteAllText(configPath, JsonConvert.SerializeObject(json, Formatting.Indented));
    }

    public static void AdjustOsuApiCredentials(int clientId, string clientSecret)
    {
        string configPath = Path.Combine(DanserGoDirectoryPath, "settings", "credentials.json");

        if (!File.Exists(configPath))
        {
            File.WriteAllText(configPath,
                JsonConvert.SerializeObject(new DanserCredentials() { ClientId = clientId.ToString(), ClientSecret = clientSecret },
                Formatting.Indented,
                new JsonSerializerSettings() { DateFormatString = "yyyy-MM-dd'T'HH:mm:ss'Z'" }));
        }

        var json = JsonConvert.DeserializeObject<dynamic>(File.ReadAllText(configPath))!;
        json["ClientId"] = $"{clientId}";
        json["ClientSecret"] = clientSecret;
        json["AccessToken"] = string.Empty;
        json["RefreshToken"] = string.Empty;

        File.WriteAllText(configPath, JsonConvert.SerializeObject(json, Formatting.Indented));
    }

    public static void AdjustDanserGoPath(OperatingSystem operatingSystem)
    {
        if (operatingSystem.Platform == PlatformID.Win32NT)
        {
            if (!DanserGoPath.EndsWith(".exe"))
            {
                DanserGoPath += ".exe";
            }
        }
    }

    public static bool DanserExists() => File.Exists(DanserGoPath);
    public static void CreateDirectoriesIfNeeded()
    {
        if (!Directory.Exists(VideosPath))
        {
            Directory.CreateDirectory(VideosPath);
        }

        if (!Directory.Exists(SongsPath))
        {
            Directory.CreateDirectory(SongsPath);
        }

        if (!Directory.Exists(ScreenshotsPath))
        {
            Directory.CreateDirectory(ScreenshotsPath);
        }
    }
}