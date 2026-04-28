using Newtonsoft.Json;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace ExperimentalRendererWrapper
{
    public class ExperimentalRenderer
    {
        public static string ExperimentalRendererPath = Path.Combine(AppContext.BaseDirectory, "experimental-renderer", "osu-replay-viewer");
        public readonly static string ExperimentalRendererDirectoryPath = Path.GetDirectoryName(ExperimentalRendererPath)!;
        public readonly static string ConfigPath = Path.Combine(ExperimentalRendererDirectoryPath, "osu-replay-viewer-config.json");

        public ExperimentalRenderer()
        {
            if (!ExperimentalRendererExists())
            {
                throw new FileNotFoundException($"Experimental renderer executable was not found at: {ExperimentalRendererPath}");
            }
        }

        public async Task<ExperimentalRendererResult> ExecuteAsync(string arguments, ConcurrentDictionary<string, string> renderUpdates, int timeoutMs = 1000_000, CancellationToken cancellationToken = default)
        {
            var processStartInfo = new ProcessStartInfo
            {
                FileName = ExperimentalRendererPath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(ExperimentalRendererPath)
            };

            using var process = new Process { StartInfo = processStartInfo };

            var outputStringBuilder = new StringBuilder();
            var errorStringBuilder = new StringBuilder();

            bool audioDecoded = false;
            process.OutputDataReceived += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data))
                    return;
                outputStringBuilder.AppendLine(e.Data);

                if (e.Data.Contains("Audio decoded in "))
                {
                    audioDecoded = true;
                }
            };

            var progressRegex = new Regex(@"time=(\d{2}):(\d{2}):(\d{2})\.(\d{2})", RegexOptions.Compiled);
            process.ErrorDataReceived += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data))
                    return;
                errorStringBuilder.AppendLine(e.Data);

                // Match progress
                var matchProgress = progressRegex.Match(e.Data);
                if (matchProgress.Success && audioDecoded)
                {
                    var hours = int.Parse(matchProgress.Groups[1].Value);
                    var minutes = int.Parse(matchProgress.Groups[2].Value);
                    var seconds = int.Parse(matchProgress.Groups[3].Value);
                    var ms = int.Parse(matchProgress.Groups[4].Value) * 10;

                    int secondsRendered = hours * 3600 + minutes * 60 + seconds + (int)Math.Round(ms / 1000.0);
                    int beatmapLength = int.Parse(renderUpdates["BeatmapLength"]);
                    renderUpdates["Progress"] = $"{secondsRendered / (double)beatmapLength}";
                }
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

                throw new TimeoutException($"Experimental renderer process timed out after {timeoutMs}ms");
            }

            string outputText = outputStringBuilder.ToString();
            string errorText = errorStringBuilder.ToString();
            bool success = process.ExitCode == 0 && outputText.Contains("Render finished in ");
            return new ExperimentalRendererResult
            {
                ExitCode = process.ExitCode,
                Output = outputText,
                Error = errorText,
                Success = success
            };
        }

        public static bool ExperimentalRendererExists() => File.Exists(ExperimentalRendererPath);
        public static void AdjustExperimentalRendererPath(OperatingSystem operatingSystem)
        {
            if (operatingSystem.Platform == PlatformID.Win32NT)
            {
                ExperimentalRendererPath += ".exe";
            }
        }
        public static void AdjustConfig(ExperimentalRendererConfiguration configuration)
        {
            File.WriteAllText(ConfigPath, JsonConvert.SerializeObject(configuration, Formatting.Indented));
        }
    }
}
