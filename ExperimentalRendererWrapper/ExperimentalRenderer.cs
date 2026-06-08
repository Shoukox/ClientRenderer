using Newtonsoft.Json;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ExperimentalRendererWrapper
{
    public static class ExperimentalRenderer
    {
        public static string ExperimentalRendererPath = Path.Combine(AppContext.BaseDirectory, "experimental-renderer", "osu-replay-viewer");
        public readonly static string ExperimentalRendererDirectoryPath = Path.GetDirectoryName(ExperimentalRendererPath)!;
        public readonly static string FfmpegPath = Path.Combine(ExperimentalRendererDirectoryPath, "ffmpeg", "ffmpeg.exe");
        public readonly static string ConfigPath = Path.Combine(ExperimentalRendererDirectoryPath, "orv_config.json");

        public static async Task<ExperimentalRendererResult> ExecuteAsync(IEnumerable<string> args, ConcurrentDictionary<string, string> renderUpdates, int timeoutMs = 1000_000, CancellationToken cancellationToken = default)
        {
            if (!ExperimentalRendererExists())
            {
                throw new FileNotFoundException($"Experimental renderer executable was not found at: {ExperimentalRendererPath}");
            }
            ProcessStartInfo processStartInfo = new ProcessStartInfo
            {
                FileName = ExperimentalRendererPath,
                WorkingDirectory = Path.GetDirectoryName(ExperimentalRendererPath),

                UseShellExecute = false,
                CreateNoWindow = true,

                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = false
            };
            foreach (string arg in args)
            {
                processStartInfo.ArgumentList.Add(arg);
            }

            using Process process = new Process { StartInfo = processStartInfo };

            StringBuilder outputStringBuilder = new StringBuilder();
            StringBuilder errorStringBuilder = new StringBuilder();

            bool audioDecoded = false;

            Regex progressRegex = new Regex(@"Progress: (\d+).(\d*)%", RegexOptions.Compiled);
            void MatchProgress(string line)
            {
                var matchProgress = progressRegex.Match(line);
                if (matchProgress.Success)
                {
                    var progress = int.Parse(matchProgress.Groups[1].Value) + int.Parse(matchProgress.Groups[2].Value) / Math.Pow(10, matchProgress.Groups[2].Value.Length);
                    renderUpdates["Progress"] = $"{progress / 100}";
                }
            }

            process.OutputDataReceived += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data))
                    return;
                outputStringBuilder.AppendLine(e.Data);

                MatchProgress(e.Data);
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data))
                    return;
                errorStringBuilder.AppendLine(e.Data);

                MatchProgress(e.Data);
            };

            process.Start();
            process.PriorityClass = ProcessPriorityClass.High;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using CancellationTokenSource timeoutCts = new CancellationTokenSource(timeoutMs);
            using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

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
                if (!ExperimentalRendererPath.EndsWith(".exe"))
                {
                    ExperimentalRendererPath += ".exe";
                }
            }
        }
        public static void AdjustConfig(ExperimentalRendererConfiguration configuration)
        {
            File.WriteAllText(ConfigPath, JsonConvert.SerializeObject(configuration, Formatting.Indented));
        }
    }
}
