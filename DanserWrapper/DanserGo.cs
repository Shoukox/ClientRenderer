using Newtonsoft.Json;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace DanserWrapper;

public class DanserGo
{
    public static string DanserGoPath = Path.Combine( Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!, "danser", "danser-cli");
    public readonly static string DanserGoDirectoryPath = Path.GetDirectoryName(DanserGoPath)!;
    public readonly static string VideosPath = Path.Combine(DanserGoDirectoryPath, "videos");
    public readonly static string SongsPath = Path.Combine(DanserGoDirectoryPath, "songs");

    public DanserGo()
    {
        if (!DanserExists())
        {
            throw new FileNotFoundException($"danser-go executable was not found at: {DanserGoPath}");
        }
        CreateDirectoriesIfNeeded();
    }

    public async Task<DanserResult> ExecuteAsync(string arguments, ConcurrentDictionary<string, string> renderUpdates, int timeoutMs = 180_000)
    {
        var processStartInfo = new ProcessStartInfo
        {
            FileName = DanserGoPath,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(DanserGoPath)
        };

        using var process = new Process { StartInfo = processStartInfo };

        var outputStringBuilder = new StringBuilder();
        var errorStringBuilder = new StringBuilder();

        var progressRegex = new Regex(@"Progress: (\d+)%", RegexOptions.Compiled);
        process.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data))
                return;
            outputStringBuilder.AppendLine(e.Data);

            var match = progressRegex.Match(e.Data);
            if (match.Success)
            {
                var progress = double.Parse(match.Groups[1].Value);
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

        var completed = await process.WaitForExitAsync(TimeSpan.FromMilliseconds(timeoutMs));
        if (!completed)
        {
            process.Kill();
            throw new TimeoutException($"danser-go process timed out after {timeoutMs}ms");
        }

        string outputText = outputStringBuilder.ToString();
        string errorText = errorStringBuilder.ToString();
        bool success = process.ExitCode == 0 && outputText.Contains("Finished!") && outputText.Contains("Video is available at:");
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
    public static void AdjustConfig(string encoder = "h264_nvenc", string skinName = "default")
    {
        string configPath = Path.Combine(DanserGoDirectoryPath, "settings", "default.json");
        var json = JsonConvert.DeserializeObject<dynamic>(File.ReadAllText(configPath))!;

        json["General"]["OsuSongsDir"] = SongsPath;
        json["General"]["OsuSkinsDir"] = Path.Combine(DanserGoDirectoryPath, "skins");
        json["General"]["OsuReplaysDir"] = Path.Combine(DanserGoDirectoryPath, "replays");
        
        json["Recording"]["Encoder"] = encoder;
        json["Recording"]["AudioCodec"] = "aac";
        json["Recording"]["FrameWidth"] = 1280;
        json["Recording"]["FrameHeight"] = 720;
        json["Recording"]["OutputDir"] = "videos";

        json["Skin"]["CurrentSkin"] = skinName;
        json["Skin"]["FallbackSkin"] = "default";

        File.WriteAllText(configPath, JsonConvert.SerializeObject(json, Formatting.Indented));
    }

    public static void AdjustDanserGoPath(OperatingSystem operatingSystem)
    {
        if (operatingSystem.Platform == PlatformID.Win32NT)
        {
            DanserGoPath += ".exe";
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
    }
}

public class DanserResult
{
    public int ExitCode { get; set; }
    public string Output { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
    public bool Success { get; set; }
}