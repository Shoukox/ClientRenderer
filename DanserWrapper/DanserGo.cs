using System.Diagnostics;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;

namespace DanserWrapper;

public class DanserGo
{
    public readonly static string DanserGoPath = Path.Combine( Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!, "danser", "danser-cli");
    public readonly static string DanserGoDirectoryPath = Path.GetDirectoryName(DanserGoPath)!;
    public readonly static string VideosPath = Path.Combine(DanserGoDirectoryPath, "Videos");

    public DanserGo()
    {
        if (!File.Exists(DanserGoPath))
        {
            throw new FileNotFoundException($"danser-go executable not found at: {DanserGoPath}");
        }
    }

    public async Task<DanserResult> ExecuteAsync(string arguments, int timeoutMs = 180_000)
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

        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();

        process.OutputDataReceived += (sender, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                outputBuilder.AppendLine(e.Data);
        };

        process.ErrorDataReceived += (sender, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                errorBuilder.AppendLine(e.Data);
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

        return new DanserResult
        {
            ExitCode = process.ExitCode,
            Output = outputBuilder.ToString(),
            Error = errorBuilder.ToString(),
            Success = process.ExitCode == 0
        };
    }

    public static void AdjustConfig()
    {
        string configPath = Path.Combine(DanserGoDirectoryPath, "settings", "default.json");
        var json = JsonConvert.DeserializeObject<dynamic>(File.ReadAllText(configPath))!;
        
        json["General"]["OsuSongsDir"] = Path.Combine(DanserGoDirectoryPath, "Songs");
        json["General"]["OsuSkinsDir"] = Path.Combine(DanserGoDirectoryPath, "Skins");
        json["General"]["OsuReplaysDir"] = Path.Combine(DanserGoDirectoryPath, "Replays");
        
        json["Recording"]["Encoder"] = "h264_nvenc";
        json["Recording"]["AudioCodec"] = "aac";
        json["Recording"]["FrameWidth"] = 1280;
        json["Recording"]["FrameHeight"] = 720;
        json["Recording"]["OutputDir"] = "Videos";
        
        File.WriteAllText(configPath, JsonConvert.SerializeObject(json, Formatting.Indented));
    }
}

public class DanserResult
{
    public int ExitCode { get; set; }
    public string Output { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
    public bool Success { get; set; }
}