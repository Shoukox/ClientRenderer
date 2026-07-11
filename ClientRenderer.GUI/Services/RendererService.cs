using ClientRenderer.Abstractions;
using ClientRenderer.Connection;
using ClientRenderer.Helpers;
using ClientRenderer.Logging;
using ClientRenderer.Render;
using ClientRenderer.RenderPipeline;
using ClientRenderer.Startup;
using DanserWrapper;
using ExperimentalRendererWrapper;
using Microsoft.Extensions.DependencyInjection;
using OsuApi.BanchoV2;
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace ClientRenderer.GUI.Services
{
    public enum RendererServiceState
    {
        Offline,
        Starting,
        Online,
        Failed
    }

    public readonly record struct RendererStatusSnapshot(RendererServiceState State, int ConsecutiveHeartbeatFailures);

    public sealed class RendererService : IDisposable
    {
        private const string DanserGoVersion = "0.11.0";
        private const string DanserGoReleaseBaseUrl = "https://github.com/Wieku/danser-go/releases/download/0.11.0";
        private const string ExperimentalRendererVersion = "0.9.0";
        private const string ExperimentalRendererReleaseBaseUrl = "https://github.com/Shoukox/osu-replay-viewer-continued/releases/download/0.9.0";

        public static RendererService Instance => _instance.Value;
        private static readonly Lazy<RendererService> _instance = new(() => new RendererService());

        private readonly object _sync = new();
        private readonly SemaphoreSlim _restartGate = new(1, 1);
        private Task? _runTask;
        private CancellationTokenSource? _runCancellationTokenSource;
        private bool _disposed;
        private string? _currentEncoder;
        private string? _currentServerUrl;
        private RendererServiceState _state = RendererServiceState.Offline;
        private int _consecutiveHeartbeatFailures;
        private ServerConnection? _serverConnection;

        public bool IsRenderingRightNow = false;

        public event Action<RendererStatusSnapshot>? StatusChanged;

        public RendererStatusSnapshot Status
        {
            get
            {
                lock (_sync)
                    return new RendererStatusSnapshot(_state, _consecutiveHeartbeatFailures);
            }
        }

        public Task RunTask(string encoder, string serverUrl)
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);

                _currentEncoder = encoder;
                _currentServerUrl = serverUrl;

                if (_runTask != null)
                    return _runTask;

                _runCancellationTokenSource = new CancellationTokenSource();
                setStatus(RendererServiceState.Starting, 0);
                _runTask = Task.Run(() => RunAsync(encoder, serverUrl, _runCancellationTokenSource.Token));
                return _runTask;
            }
        }

        public async Task RestartAsync(bool waitForOnline = true)
        {
            await _restartGate.WaitAsync().ConfigureAwait(false);
            try
            {
                string encoder;
                string serverUrl;

                lock (_sync)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    encoder = _currentEncoder ?? App.SettingsProvider.Current.DefaultEncoder;
                    serverUrl = _currentServerUrl ?? App.SettingsProvider.Current.ServerUrl;
                }

                await stopInternalAsync().ConfigureAwait(false);
                Logger.Log("Renderer service stopped. Restarting...");
                _ = RunTask(encoder, serverUrl);

                if (waitForOnline)
                {
                    while (_state is not RendererServiceState.Online and not RendererServiceState.Failed)
                    {
                        await Task.Delay(500).ConfigureAwait(false);
                        lock (_sync)
                        {
                            if (_disposed)
                                return;
                        }
                    }
                }
            }
            finally
            {
                _restartGate.Release();
            }
        }

        private async Task RunAsync(string encoder, string serverUrl, CancellationToken cancellationToken)
        {
            try
            {
                using var bootstrapServices = new ServiceCollection()
                    .AddSingleton<IConfigurationLoader, ConfigurationLoader>()
                    .BuildServiceProvider();

                var appConfig = await bootstrapServices.GetRequiredService<IConfigurationLoader>().LoadAsync();

                await ValidateRenderingDependenciesAsync(appConfig.OsuApiV2Configuration.ClientId, appConfig.OsuApiV2Configuration.ClientSecret, cancellationToken);

                Logger.Log($"{encoder} has been set as the default danser encoder.");

                ServerConnection serverConnection = new ServerConnection(serverUrl, appConfig.RendererCredentials, cancellationToken);
                serverConnection.HeartbeatStatusChanged += OnHeartbeatStatusChanged;

                lock (_sync)
                    _serverConnection = serverConnection;

                await using var runtimeServices = new ServiceCollection()
                    .AddSingleton(appConfig)
                    .AddSingleton(new BanchoApiV2(appConfig.OsuApiV2Configuration.ClientId, appConfig.OsuApiV2Configuration.ClientSecret))
                    .AddSingleton<IServerConnection>(_ => serverConnection)
                    .AddSingleton<IReplaysDownloader, ReplaysDownloader>()
                    .AddSingleton<IBeatmapsetsDownloader>(sp => new BeatmapsetsDownloader(sp.GetRequiredService<BanchoApiV2>(), appConfig.OsuSessionCookie))
                    .AddSingleton<ISkinsDownloader, SkinsDownloader>()
                    .AddSingleton<IThumbnailRenderer, ThumbnailRenderer>()
                    .AddSingleton<IVideoRenderer, VideoRenderer>()
                    .AddSingleton<IAutomaticUpdateService, VelopackAutomaticUpdateService>()
                    .AddSingleton<IRenderWorker>(sp => new RenderWorker(
                        sp.GetRequiredService<IVideoRenderer>(),
                        sp.GetRequiredService<IServerConnection>(),
                        encoder,
                        sp.GetRequiredService<IAutomaticUpdateService>()))
                    .BuildServiceProvider();

                Logger.Log("osu! api v2 credentials loaded. They will be validated on the first real API request.");

                while (!await serverConnection.InitializeToken() && !cancellationToken.IsCancellationRequested)
                {
                    Logger.LogWarning("Failed to initialize access token. Retrying in 5 seconds. Check renderer-settings.json and the internet connection.");
                    await Task.Delay(5000, cancellationToken);
                }

                IRenderWorker renderWorker = runtimeServices.GetRequiredService<IRenderWorker>();
                renderWorker.RenderingStatus += isRendering =>
                {
                    IsRenderingRightNow = isRendering;
                    Logger.Log($"Rendering status changed: {(isRendering ? "Rendering" : "Idle")}");
                };
                if (!cancellationToken.IsCancellationRequested)
                    await renderWorker.RunAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Logger.Log("Renderer service was canceled.");
            }
            catch (InvalidOperationException e)
            {
                Logger.LogError(e, "Renderer service failed because the configuration or runtime dependencies are invalid.");
                setStatus(RendererServiceState.Failed, 0);
            }
            catch (Exception e)
            {
                Logger.LogError(e, "Unhandled renderer service exception.");
                File.WriteAllText("error.txt", $"Crash: {e}");
                Logger.LogError("Unhandled renderer service exception was written to error.txt.");
                setStatus(RendererServiceState.Failed, 0);
            }
            finally
            {
                lock (_sync)
                {
                    if (_serverConnection != null)
                    {
                        _serverConnection.HeartbeatStatusChanged -= OnHeartbeatStatusChanged;
                        _serverConnection = null;
                    }

                    _runTask = null;
                    _runCancellationTokenSource?.Dispose();
                    _runCancellationTokenSource = null;
                }

                if (_state != RendererServiceState.Failed)
                {
                    setStatus(RendererServiceState.Offline, 0);
                }
            }
        }

        private void OnHeartbeatStatusChanged(HeartbeatStatus status)
        {
            setStatus(status.IsOnline ? RendererServiceState.Online : RendererServiceState.Offline, status.ConsecutiveFailures);
        }

        private async Task stopInternalAsync()
        {
            Task? runTask;
            CancellationTokenSource? cancellationTokenSource;

            lock (_sync)
            {
                runTask = _runTask;
                cancellationTokenSource = _runCancellationTokenSource;
            }

            cancellationTokenSource?.Cancel();
            Logger.Log("Renderer service stop requested.");

            if (runTask != null)
            {
                try
                {
                    await runTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Logger.Log("Renderer service task was canceled.");
                }
                catch (AggregateException ex) when (ex.InnerExceptions.Count == 1 && ex.InnerException is OperationCanceledException)
                {
                    Logger.Log("Renderer service task was canceled.");
                }
            }
        }

        private void setStatus(RendererServiceState state, int consecutiveHeartbeatFailures)
        {
            Action<RendererStatusSnapshot>? handler = null;
            RendererStatusSnapshot snapshot;
            bool changed;

            lock (_sync)
            {
                changed = _state != state || _consecutiveHeartbeatFailures != consecutiveHeartbeatFailures;
                _state = state;
                _consecutiveHeartbeatFailures = consecutiveHeartbeatFailures;
                snapshot = new RendererStatusSnapshot(_state, _consecutiveHeartbeatFailures);
                if (changed)
                    handler = StatusChanged;
            }

            if (changed)
                handler?.Invoke(snapshot);
        }

        private static async Task ValidateRenderingDependenciesAsync(int osuClientId, string osuClientSecret, CancellationToken cancellationToken)
        {
            DanserGo.AdjustDanserGoPath(Environment.OSVersion);
            if (!DanserGo.DanserExists())
                await DownloadAndExtractDanserGoAsync(cancellationToken);
            Logger.Log($"danser-go executable found at: {DanserGo.DanserGoPath}");
            await EnsureDanserGoSettingsInitializedAsync(cancellationToken);
            DanserGo.AdjustOsuApiCredentials(osuClientId, osuClientSecret);

            ExperimentalRenderer.AdjustExperimentalRendererPath(Environment.OSVersion);
            if (!ExperimentalRenderer.ExperimentalRendererExists())
                await DownloadAndExtractExperimentalRendererAsync(cancellationToken);
            Logger.Log($"Experimental renderer executable found at: {ExperimentalRenderer.ExperimentalRendererPath}");

            if (OperatingSystem.IsWindows())
            {
                WindowsGpuPreferenceHelper.SetHighPerformanceForExecutables([
                    DanserGo.DanserGoPath,
                    ExperimentalRenderer.ExperimentalRendererPath,
                    Path.Combine(DanserGo.DanserGoDirectoryPath, "ffmpeg", "ffmpeg.exe"),
                    Path.Combine(DanserGo.DanserGoDirectoryPath, "ffmpeg", "ffprobe.exe"),
                    Path.Combine(DanserGo.DanserGoDirectoryPath, "ffmpeg", "ffplay.exe"),
                    Path.Combine(ExperimentalRenderer.ExperimentalRendererDirectoryPath, "ffmpeg", "ffmpeg.exe"),
                    Path.Combine(ExperimentalRenderer.ExperimentalRendererDirectoryPath, "ffmpeg", "ffprobe.exe"),
                    Path.Combine(ExperimentalRenderer.ExperimentalRendererDirectoryPath, "ffmpeg", "ffplay.exe")
                ]);
            }

            DanserGo.CreateDirectoriesIfNeeded();
            Logger.Log("Rendering dependencies validated.");
        }

        private static async Task DownloadAndExtractDanserGoAsync(CancellationToken cancellationToken)
        {
            if (Directory.Exists(DanserGo.DanserGoDirectoryPath))
            {
                Directory.Delete(DanserGo.DanserGoDirectoryPath, true);
            }
            
            string downloadUrl = GetDanserGoDownloadUrl();
            string archivePath = Path.Combine(Path.GetTempPath(), $"danser-go-{DanserGoVersion}-{Guid.NewGuid():N}.zip");

            Logger.LogWarning($"danser-go was not found at: {DanserGo.DanserGoPath}");
            Logger.Log($"Downloading danser-go {DanserGoVersion} from: {downloadUrl}");

            try
            {
                using HttpClient httpClient = new();
                await DownloadFileWithProgressAsync(httpClient, downloadUrl, archivePath, "danser-go", cancellationToken);

                Directory.CreateDirectory(DanserGo.DanserGoDirectoryPath);
                Logger.Log($"Extracting danser-go archive to: {DanserGo.DanserGoDirectoryPath}");
                ZipFile.ExtractToDirectory(archivePath, DanserGo.DanserGoDirectoryPath, overwriteFiles: true);
                EnsureDanserGoExecutablePermissions();

                if (!DanserGo.DanserExists())
                    throw new FileNotFoundException($"danser-go archive was extracted, but the executable was not found at: {DanserGo.DanserGoPath}");

                Logger.Log($"danser-go {DanserGoVersion} was downloaded and extracted successfully.");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to download or extract danser-go.");
                throw;
            }
            finally
            {
                if (File.Exists(archivePath))
                    File.Delete(archivePath);
            }
        }

        private static string GetDanserGoDownloadUrl()
        {
            string archiveName;
            if (OperatingSystem.IsWindows())
            {
                archiveName = $"danser-{DanserGoVersion}-win.zip";
            }
            else if (OperatingSystem.IsLinux())
            {
                archiveName = $"danser-{DanserGoVersion}-linux.zip";
            }
            else
            {
                throw new PlatformNotSupportedException("danser-go 0.11.0 only provides Windows and Linux release archives.");
            }

            return $"{DanserGoReleaseBaseUrl}/{archiveName}";
        }

        private static async Task DownloadFileWithProgressAsync(HttpClient httpClient, string downloadUrl, string destinationPath, string displayName, CancellationToken cancellationToken)
        {
            using HttpResponseMessage response = await httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            long? totalBytes = response.Content.Headers.ContentLength;
            await using Stream downloadStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using FileStream outputStream = File.Create(destinationPath);

            byte[] buffer = new byte[128 * 1024];
            long downloadedBytes = 0;
            int lastLoggedPercent = -1;
            DateTime lastLogTimeUtc = DateTime.MinValue;

            Logger.Log(totalBytes is > 0
                ? $"Downloading {displayName}: 0.00% (0.00 MB / {FormatMegabytes(totalBytes.Value)} MB)"
                : $"Downloading {displayName}: 0.00 MB");

            while (!cancellationToken.IsCancellationRequested)
            {
                int bytesRead = await downloadStream.ReadAsync(buffer, cancellationToken);
                if (bytesRead == 0)
                    break;

                await outputStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                downloadedBytes += bytesRead;

                if (ShouldLogDownloadProgress(totalBytes, downloadedBytes, ref lastLoggedPercent, ref lastLogTimeUtc))
                    LogDownloadProgress(displayName, downloadedBytes, totalBytes);
            }

            if (totalBytes is not > 0 || lastLoggedPercent < 100)
                LogDownloadProgress(displayName, downloadedBytes, totalBytes);
        }

        private static bool ShouldLogDownloadProgress(long? totalBytes, long downloadedBytes, ref int lastLoggedPercent, ref DateTime lastLogTimeUtc)
        {
            DateTime now = DateTime.UtcNow;

            if (totalBytes is > 0)
            {
                int currentPercent = (int)Math.Floor(downloadedBytes * 100.0 / totalBytes.Value);
                if (currentPercent >= 100)
                {
                    bool shouldLog = lastLoggedPercent < 100;
                    lastLoggedPercent = 100;
                    lastLogTimeUtc = now;
                    return shouldLog;
                }

                if (currentPercent >= lastLoggedPercent + 5)
                {
                    lastLoggedPercent = currentPercent;
                    lastLogTimeUtc = now;
                    return true;
                }

                return false;
            }

            if (now - lastLogTimeUtc < TimeSpan.FromSeconds(1))
                return false;

            lastLogTimeUtc = now;
            return true;
        }

        private static void LogDownloadProgress(string displayName, long downloadedBytes, long? totalBytes)
        {
            Logger.Log(totalBytes is > 0
                ? $"Downloading {displayName}: {downloadedBytes * 100.0 / totalBytes.Value:0.00}% ({FormatMegabytes(downloadedBytes)} MB / {FormatMegabytes(totalBytes.Value)} MB)"
                : $"Downloading {displayName}: {FormatMegabytes(downloadedBytes)} MB");
        }

        private static string FormatMegabytes(long bytes)
        {
            return (bytes / 1024.0 / 1024.0).ToString("0.00");
        }

        private static async Task EnsureDanserGoSettingsInitializedAsync(CancellationToken cancellationToken)
        {
            string settingsDirectory = Path.Combine(DanserGo.DanserGoDirectoryPath, "settings");
            string defaultSettingsPath = Path.Combine(settingsDirectory, "default.json");

            if (File.Exists(defaultSettingsPath))
                return;

            Logger.Log($"danser-go settings were not found at: {settingsDirectory}. Running danser-go once to initialize them.");

            using Process process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = DanserGo.DanserGoPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = false,
                    WorkingDirectory = DanserGo.DanserGoDirectoryPath
                }
            };

            process.Start();
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

            try
            {
                using CancellationTokenSource timeoutCts = new(TimeSpan.FromSeconds(30));
                using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

                while (!File.Exists(defaultSettingsPath) && !process.HasExited)
                    await Task.Delay(250, linkedCts.Token);

                if (File.Exists(defaultSettingsPath))
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync(cancellationToken);
                    }

                    Logger.Log($"danser-go settings initialized at: {settingsDirectory}");
                    return;
                }

                await process.WaitForExitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);

                throw;
            }
            catch (OperationCanceledException ex)
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);

                throw new TimeoutException("danser-go did not create its settings directory within 30 seconds.", ex);
            }

            if (!File.Exists(defaultSettingsPath))
            {
                string output = outputTask.IsCompletedSuccessfully ? outputTask.Result : string.Empty;
                string error = errorTask.IsCompletedSuccessfully ? errorTask.Result : string.Empty;
                throw new InvalidOperationException($"danser-go exited without creating {defaultSettingsPath}. Exit code: {process.ExitCode}. Output: {output}. Error: {error}");
            }
        }

        private static async Task DownloadAndExtractExperimentalRendererAsync(CancellationToken cancellationToken)
        {
            if (Directory.Exists(ExperimentalRenderer.ExperimentalRendererDirectoryPath))
            {
                Directory.Delete(ExperimentalRenderer.ExperimentalRendererDirectoryPath, true);
            }
            
            string downloadUrl = GetExperimentalRendererDownloadUrl();
            string archivePath = Path.Combine(Path.GetTempPath(), $"experimental-renderer-{ExperimentalRendererVersion}-{Guid.NewGuid():N}.zip");

            Logger.LogWarning($"Experimental renderer was not found at: {ExperimentalRenderer.ExperimentalRendererPath}");
            Logger.Log($"Downloading experimental renderer {ExperimentalRendererVersion} from: {downloadUrl}");

            try
            {
                using HttpClient httpClient = new();
                await DownloadFileWithProgressAsync(httpClient, downloadUrl, archivePath, "experimental renderer", cancellationToken);

                Directory.CreateDirectory(ExperimentalRenderer.ExperimentalRendererDirectoryPath);
                Logger.Log($"Extracting experimental renderer archive to: {ExperimentalRenderer.ExperimentalRendererDirectoryPath}");
                ExtractExperimentalRendererArchive(archivePath, cancellationToken);

                if (!ExperimentalRenderer.ExperimentalRendererExists())
                    throw new FileNotFoundException($"Experimental renderer archive was extracted, but the executable was not found at: {ExperimentalRenderer.ExperimentalRendererPath}");

                EnsureExperimentalRendererExecutablePermissions();
                Logger.Log($"Experimental renderer {ExperimentalRendererVersion} was downloaded and extracted successfully.");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to download or extract experimental renderer.");
                throw;
            }
            finally
            {
                if (File.Exists(archivePath))
                    File.Delete(archivePath);
            }
        }

        private static void ExtractExperimentalRendererArchive(string archivePath, CancellationToken cancellationToken)
        {
            string stagingDirectoryPath = Path.Combine(Path.GetTempPath(), $"experimental-renderer-extract-{Guid.NewGuid():N}");

            try
            {
                Directory.CreateDirectory(stagingDirectoryPath);

                cancellationToken.ThrowIfCancellationRequested();
                ZipFile.ExtractToDirectory(archivePath, stagingDirectoryPath, overwriteFiles: true);
                cancellationToken.ThrowIfCancellationRequested();

                string sourceDirectoryPath = GetExperimentalRendererStagingSourceDirectory(stagingDirectoryPath);
                CopyDirectoryContents(sourceDirectoryPath, ExperimentalRenderer.ExperimentalRendererDirectoryPath);
                Logger.Log($"Experimental renderer archive extracted successfully. Files copied from: {sourceDirectoryPath}");
            }
            finally
            {
                if (Directory.Exists(stagingDirectoryPath))
                    Directory.Delete(stagingDirectoryPath, true);
            }
        }

        private static string GetExperimentalRendererStagingSourceDirectory(string stagingDirectoryPath)
        {
            string[] candidateRoots =
            [
                Path.Combine(stagingDirectoryPath, "win-x64"),
                Path.Combine(stagingDirectoryPath, "linux-x64")
            ];

            string? sourceDirectoryPath = candidateRoots.FirstOrDefault(Directory.Exists);
            return sourceDirectoryPath ?? stagingDirectoryPath;
        }

        private static void CopyDirectoryContents(string sourceDirectoryPath, string destinationDirectoryPath)
        {
            string sourceRoot = Path.GetFullPath(sourceDirectoryPath);
            string destinationRoot = Path.GetFullPath(destinationDirectoryPath);
            Directory.CreateDirectory(destinationRoot);

            foreach (string directoryPath in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories))
            {
                string relativePath = Path.GetRelativePath(sourceRoot, directoryPath);
                if (relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p == "." || p == ".."))
                    throw new InvalidDataException($"Experimental renderer archive directory escapes the staging directory: {directoryPath}");

                Directory.CreateDirectory(Path.Combine(destinationRoot, relativePath));
            }

            foreach (string filePath in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
            {
                string relativePath = Path.GetRelativePath(sourceRoot, filePath);
                if (relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p == "." || p == ".."))
                    throw new InvalidDataException($"Experimental renderer archive file escapes the staging directory: {filePath}");

                string destinationPath = Path.Combine(destinationRoot, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                File.Copy(filePath, destinationPath, overwrite: true);
            }
        }

        private static string GetExperimentalRendererDownloadUrl()
        {
            string archiveName;
            if (OperatingSystem.IsWindows())
            {
                archiveName = "experimental-renderer.win-x64.zip";
            }
            else if (OperatingSystem.IsLinux())
            {
                archiveName = "experimental-renderer.linux-x64.zip";
            }
            else
            {
                throw new PlatformNotSupportedException("Experimental renderer 0.9.0 only provides Windows x64 and Linux x64 release archives.");
            }

            return $"{ExperimentalRendererReleaseBaseUrl}/{archiveName}";
        }

        private static void EnsureExperimentalRendererExecutablePermissions()
        {
            if (!OperatingSystem.IsLinux())
                return;

            UnixFileMode executableMode =
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

            SetExecutableModeIfExists(ExperimentalRenderer.ExperimentalRendererPath, executableMode);
            SetExecutableModeIfExists(Path.Combine(ExperimentalRenderer.ExperimentalRendererDirectoryPath, "osu-replay-viewer"), executableMode);
            SetExecutableModeIfExists(Path.Combine(ExperimentalRenderer.ExperimentalRendererDirectoryPath, "ffmpeg", "ffmpeg"), executableMode);
            SetExecutableModeIfExists(Path.Combine(ExperimentalRenderer.ExperimentalRendererDirectoryPath, "ffmpeg", "ffprobe"), executableMode);
            SetExecutableModeIfExists(Path.Combine(ExperimentalRenderer.ExperimentalRendererDirectoryPath, "ffmpeg", "ffplay"), executableMode);
        }

        private static void EnsureDanserGoExecutablePermissions()
        {
            if (!OperatingSystem.IsLinux())
                return;

            UnixFileMode executableMode =
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

            SetExecutableModeIfExists(DanserGo.DanserGoPath, executableMode);
            SetExecutableModeIfExists(Path.Combine(DanserGo.DanserGoDirectoryPath, "danser"), executableMode);
            SetExecutableModeIfExists(Path.Combine(DanserGo.DanserGoDirectoryPath, "ffmpeg", "ffmpeg"), executableMode);
            SetExecutableModeIfExists(Path.Combine(DanserGo.DanserGoDirectoryPath, "ffmpeg", "ffprobe"), executableMode);
        }

        [SupportedOSPlatform("linux")]
        private static void SetExecutableModeIfExists(string path, UnixFileMode mode)
        {
            if (File.Exists(path))
            {
                File.SetUnixFileMode(path, mode);
                Logger.Log($"Executable permission set for: {path}");
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                    return;

                _disposed = true;
            }

            stopInternalAsync().GetAwaiter().GetResult();
            _restartGate.Dispose();
        }
    }
}
