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
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;
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
        // This is the version used by older ClientRenderer builds that did not
        // write a local dependency marker. It lets the first run of this
        // updater detect a newer experimental-renderer release.
        private const string ExperimentalRendererLegacyInstalledVersion = "0.9.0";
        private const string ExperimentalRendererFallbackVersion = "0.9.1";
        private const string ExperimentalRendererFallbackReleaseBaseUrl =
            "https://github.com/Shoukox/osu-replay-viewer-continued/releases/download/v0.9.1";
        private const string ExperimentalRendererLatestReleaseApiUrl =
            "https://api.github.com/repos/Shoukox/osu-replay-viewer-continued/releases/latest";
        private const string ExperimentalRendererVersionFileName = ".renderer-version";
        private static readonly Regex ExperimentalRendererVersionRegex =
            new("^[0-9]+\\.[0-9]+\\.[0-9]+(?:[-+][0-9A-Za-z.-]+)?$", RegexOptions.Compiled);

        private sealed record ExperimentalRendererRelease(string Version, string DownloadUrl, string AssetName);

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

                await ValidateRenderingDependenciesAsync(appConfig.OsuApiV2Configuration.ClientId,
                    appConfig.OsuApiV2Configuration.ClientSecret, cancellationToken);

                Logger.Log($"{encoder} has been set as the default danser encoder.");

                ServerConnection serverConnection =
                    new ServerConnection(serverUrl, appConfig.RendererCredentials, cancellationToken);
                serverConnection.HeartbeatStatusChanged += OnHeartbeatStatusChanged;

                lock (_sync)
                    _serverConnection = serverConnection;

                await using var runtimeServices = new ServiceCollection()
                    .AddSingleton(appConfig)
                    .AddSingleton(new BanchoApiV2(appConfig.OsuApiV2Configuration.ClientId,
                        appConfig.OsuApiV2Configuration.ClientSecret))
                    .AddSingleton<IServerConnection>(_ => serverConnection)
                    .AddSingleton<IReplaysDownloader, ReplaysDownloader>()
                    .AddSingleton<IBeatmapsetsDownloader>(sp =>
                        new BeatmapsetsDownloader(sp.GetRequiredService<BanchoApiV2>(), appConfig.OsuSessionCookie))
                    .AddSingleton<ISkinsDownloader, SkinsDownloader>()
                    .AddSingleton<IThumbnailRenderer, ThumbnailRenderer>()
                    .AddSingleton<IVideoRenderer, VideoRenderer>()
                    .AddSingleton<IAutomaticUpdateService, VelopackAutomaticUpdateService>()
                    .AddSingleton<IRenderWorker>(sp => new RenderWorker(
                        sp.GetRequiredService<IVideoRenderer>(),
                        sp.GetRequiredService<IServerConnection>(),
                        encoder,
                        sp.GetRequiredService<IAutomaticUpdateService>(),
                        EnsureExperimentalRendererUpToDateAsync))
                    .BuildServiceProvider();

                Logger.Log("osu! api v2 credentials loaded. They will be validated on the first real API request.");

                while (!await serverConnection.InitializeToken() && !cancellationToken.IsCancellationRequested)
                {
                    Logger.LogWarning(
                        "Failed to initialize access token. Retrying in 5 seconds. Check renderer-settings.json and the internet connection.");
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
                Logger.LogError(e,
                    "Renderer service failed because the configuration or runtime dependencies are invalid.");
                setStatus(RendererServiceState.Failed, 0);
            }
            catch (Exception e)
            {
                Logger.LogError(e, "Unhandled renderer service exception.");
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
            setStatus(status.IsOnline ? RendererServiceState.Online : RendererServiceState.Offline,
                status.ConsecutiveFailures);
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
                catch (AggregateException ex) when (ex.InnerExceptions.Count == 1 &&
                                                    ex.InnerException is OperationCanceledException)
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

        private static async Task ValidateRenderingDependenciesAsync(int osuClientId, string osuClientSecret,
            CancellationToken cancellationToken)
        {
            DanserGo.AdjustDanserGoPath(Environment.OSVersion);
            if (!DanserGo.DanserExists())
                await DownloadAndExtractDanserGoAsync(cancellationToken);
            Logger.Log($"danser-go executable found at: {DanserGo.DanserGoPath}");
            await EnsureDanserGoSettingsInitializedAsync(cancellationToken);
            DanserGo.AdjustOsuApiCredentials(osuClientId, osuClientSecret);

            ExperimentalRenderer.AdjustExperimentalRendererPath(Environment.OSVersion);
            await EnsureExperimentalRendererUpToDateAsync(cancellationToken);

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
            string archivePath =
                Path.Combine(Path.GetTempPath(), $"danser-go-{DanserGoVersion}-{Guid.NewGuid():N}.zip");

            Logger.LogWarning($"danser-go was not found at: {DanserGo.DanserGoPath}");
            Logger.Log($"Downloading danser-go {DanserGoVersion} from: {downloadUrl}");

            try
            {
                using HttpClient httpClient = new();
                await DownloadFileWithProgressAsync(httpClient, downloadUrl, archivePath, "danser-go",
                    cancellationToken);

                Directory.CreateDirectory(DanserGo.DanserGoDirectoryPath);
                Logger.Log($"Extracting danser-go archive to: {DanserGo.DanserGoDirectoryPath}");
                ZipFile.ExtractToDirectory(archivePath, DanserGo.DanserGoDirectoryPath, overwriteFiles: true);
                EnsureDanserGoExecutablePermissions();

                if (!DanserGo.DanserExists())
                    throw new FileNotFoundException(
                        $"danser-go archive was extracted, but the executable was not found at: {DanserGo.DanserGoPath}");

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
                throw new PlatformNotSupportedException(
                    "danser-go 0.11.0 only provides Windows and Linux release archives.");
            }

            return $"{DanserGoReleaseBaseUrl}/{archiveName}";
        }

        private static async Task DownloadFileWithProgressAsync(HttpClient httpClient, string downloadUrl,
            string destinationPath, string displayName, CancellationToken cancellationToken)
        {
            using HttpResponseMessage response = await httpClient.GetAsync(downloadUrl,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
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

        private static bool ShouldLogDownloadProgress(long? totalBytes, long downloadedBytes, ref int lastLoggedPercent,
            ref DateTime lastLogTimeUtc)
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
            string defaultDanserConfig = "default.json";
            string settingsDirectory = Path.Combine(DanserGo.DanserGoDirectoryPath, "settings");
            string defaultSettingsPath = Path.Combine(settingsDirectory, defaultDanserConfig);

            if (File.Exists(defaultSettingsPath))
                return;

            Logger.Log(
                $"danser-go settings were not found at: {settingsDirectory}. Running danser-go once to initialize them.");

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
                using CancellationTokenSource linkedCts =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

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
                    
                    File.Delete(defaultSettingsPath);
                    File.Copy(Path.Combine(AppContext.BaseDirectory, defaultDanserConfig), defaultSettingsPath);
                    Logger.Log($"danser-go settings were updated.");
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
                throw new InvalidOperationException(
                    $"danser-go exited without creating {defaultSettingsPath}. Exit code: {process.ExitCode}. Output: {output}. Error: {error}");
            }
        }

        private static async Task EnsureExperimentalRendererUpToDateAsync(CancellationToken cancellationToken)
        {
            bool rendererExists = ExperimentalRenderer.ExperimentalRendererExists();
            ExperimentalRendererRelease release;

            try
            {
                release = await GetLatestExperimentalRendererReleaseAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (rendererExists)
                {
                    Logger.LogWarning(
                        $"Could not check the latest experimental renderer release; keeping the installed copy. {ex.Message}");
                    Logger.Log($"Experimental renderer executable found at: {ExperimentalRenderer.ExperimentalRendererPath}");
                    return;
                }

                // Preserve first-run behaviour if GitHub's API is temporarily
                // unavailable. The pinned fallback asset is still available.
                Logger.LogWarning(
                    $"Could not query the latest experimental renderer release; trying the fallback {ExperimentalRendererFallbackVersion} asset. {ex.Message}");
                release = GetFallbackExperimentalRendererRelease();
            }

            string? installedVersion = ReadInstalledExperimentalRendererVersion();
            if (rendererExists && installedVersion == null)
            {
                // Builds before the updater existed always downloaded this
                // exact version, so an absent marker is safely migratable.
                installedVersion = ExperimentalRendererLegacyInstalledVersion;
            }

            if (rendererExists && string.Equals(installedVersion, release.Version, StringComparison.OrdinalIgnoreCase))
            {
                WriteInstalledExperimentalRendererVersion(release.Version);
                Logger.Log(
                    $"Experimental renderer {release.Version} is already installed at: {ExperimentalRenderer.ExperimentalRendererPath}");
                return;
            }

            if (rendererExists)
            {
                Logger.Log(
                    $"Updating experimental renderer from {installedVersion ?? "unknown"} to {release.Version}.");
            }
            else
            {
                Logger.LogWarning(
                    $"Experimental renderer was not found at: {ExperimentalRenderer.ExperimentalRendererPath}");
            }

            try
            {
                await DownloadAndExtractExperimentalRendererAsync(release, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (rendererExists)
            {
                Logger.LogWarning(
                    $"Could not update the experimental renderer; keeping the installed copy. {ex.Message}");
                Logger.Log($"Experimental renderer executable found at: {ExperimentalRenderer.ExperimentalRendererPath}");
                return;
            }
            Logger.Log($"Experimental renderer executable found at: {ExperimentalRenderer.ExperimentalRendererPath}");
        }

        private static async Task<ExperimentalRendererRelease> GetLatestExperimentalRendererReleaseAsync(
            CancellationToken cancellationToken)
        {
            string runtimeIdentifier = GetExperimentalRendererRuntimeIdentifier();

            using HttpClient httpClient = new();
            httpClient.Timeout = TimeSpan.FromSeconds(15);
            httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("SosuBot.ClientRenderer");

            using HttpResponseMessage response = await httpClient.GetAsync(
                ExperimentalRendererLatestReleaseApiUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            await using Stream responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using JsonDocument document = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);

            JsonElement root = document.RootElement;
            string version = NormalizeExperimentalRendererVersion(root.GetProperty("tag_name").GetString() ?? string.Empty);

            foreach (JsonElement asset in root.GetProperty("assets").EnumerateArray())
            {
                string assetName = asset.GetProperty("name").GetString() ?? string.Empty;
                if (!IsSupportedExperimentalRendererAsset(assetName, runtimeIdentifier))
                    continue;

                string downloadUrl = asset.GetProperty("browser_download_url").GetString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(downloadUrl))
                    continue;

                return new ExperimentalRendererRelease(version, downloadUrl, assetName);
            }

            throw new InvalidDataException(
                $"The experimental renderer release {version} has no {runtimeIdentifier} asset.");
        }

        private static bool IsSupportedExperimentalRendererAsset(string assetName, string runtimeIdentifier)
        {
            if (assetName.Equals($"experimental-renderer.{runtimeIdentifier}.zip", StringComparison.OrdinalIgnoreCase))
                return true;

            if (!assetName.StartsWith("osu-replay-viewer-", StringComparison.OrdinalIgnoreCase))
                return false;

            return assetName.EndsWith($"-{runtimeIdentifier}.zip", StringComparison.OrdinalIgnoreCase) ||
                   (runtimeIdentifier == "linux-x64" &&
                    assetName.EndsWith($"-{runtimeIdentifier}.tar.gz", StringComparison.OrdinalIgnoreCase));
        }

        private static string GetExperimentalRendererRuntimeIdentifier()
        {
            if (OperatingSystem.IsWindows())
                return "win-x64";

            if (OperatingSystem.IsLinux())
                return "linux-x64";

            throw new PlatformNotSupportedException(
                "The experimental renderer currently provides Windows x64 and Linux x64 release archives.");
        }

        private static ExperimentalRendererRelease GetFallbackExperimentalRendererRelease()
        {
            string runtimeIdentifier = GetExperimentalRendererRuntimeIdentifier();
            string assetName = runtimeIdentifier switch
            {
                "win-x64" => $"osu-replay-viewer-v{ExperimentalRendererFallbackVersion}-win-x64.zip",
                "linux-x64" => $"osu-replay-viewer-v{ExperimentalRendererFallbackVersion}-linux-x64.tar.gz",
                _ => throw new PlatformNotSupportedException(
                    $"The experimental renderer fallback does not provide an archive for {runtimeIdentifier}.")
            };
            string downloadUrl = $"{ExperimentalRendererFallbackReleaseBaseUrl}/{assetName}";
            return new ExperimentalRendererRelease(ExperimentalRendererFallbackVersion, downloadUrl, assetName);
        }

        private static string? ReadInstalledExperimentalRendererVersion()
        {
            string versionPath = Path.Combine(
                ExperimentalRenderer.ExperimentalRendererDirectoryPath,
                ExperimentalRendererVersionFileName);

            if (!File.Exists(versionPath))
                return null;

            try
            {
                return NormalizeExperimentalRendererVersion(File.ReadAllText(versionPath));
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"Ignoring invalid experimental renderer version marker: {ex.Message}");
                return null;
            }
        }

        private static string NormalizeExperimentalRendererVersion(string version)
        {
            string normalized = version.Trim();
            if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                normalized = normalized[1..];

            if (!ExperimentalRendererVersionRegex.IsMatch(normalized))
                throw new InvalidDataException($"Invalid experimental renderer version '{version}'.");

            return normalized;
        }

        private static void WriteInstalledExperimentalRendererVersion(string version)
        {
            Directory.CreateDirectory(ExperimentalRenderer.ExperimentalRendererDirectoryPath);
            File.WriteAllText(
                Path.Combine(ExperimentalRenderer.ExperimentalRendererDirectoryPath, ExperimentalRendererVersionFileName),
                version + Environment.NewLine);
        }

        private static async Task DownloadAndExtractExperimentalRendererAsync(
            ExperimentalRendererRelease release,
            CancellationToken cancellationToken)
        {
            string archiveExtension = release.AssetName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
                ? ".tar.gz"
                : ".zip";
            string archivePath = Path.Combine(
                Path.GetTempPath(),
                $"experimental-renderer-{release.Version}-{Guid.NewGuid():N}{archiveExtension}");
            string extractionDirectoryPath = Path.Combine(
                Path.GetTempPath(),
                $"experimental-renderer-extract-{Guid.NewGuid():N}");
            string installDirectoryPath = Path.Combine(
                Path.GetTempPath(),
                $"experimental-renderer-install-{Guid.NewGuid():N}");

            Logger.Log($"Downloading experimental renderer {release.Version} from: {release.DownloadUrl}");

            try
            {
                using HttpClient httpClient = new();
                await DownloadFileWithProgressAsync(httpClient, release.DownloadUrl, archivePath,
                    "experimental renderer", cancellationToken);

                ExtractExperimentalRendererArchive(archivePath, extractionDirectoryPath, cancellationToken);
                string sourceDirectoryPath = GetExperimentalRendererStagingSourceDirectory(extractionDirectoryPath);

                CopyDirectoryContents(sourceDirectoryPath, installDirectoryPath);
                string expectedExecutable = Path.Combine(
                    installDirectoryPath,
                    Path.GetFileName(ExperimentalRenderer.ExperimentalRendererPath));
                if (!File.Exists(expectedExecutable))
                {
                    throw new FileNotFoundException(
                        $"Experimental renderer archive was extracted, but the executable was not found at: {expectedExecutable}");
                }

                ReplaceExperimentalRendererDirectory(installDirectoryPath);
                WriteInstalledExperimentalRendererVersion(release.Version);
                EnsureExperimentalRendererExecutablePermissions();
                Logger.Log(
                    $"Experimental renderer {release.Version} was downloaded and extracted successfully.");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, $"Failed to download or extract experimental renderer {release.Version}.");
                throw;
            }
            finally
            {
                if (File.Exists(archivePath))
                    File.Delete(archivePath);
                if (Directory.Exists(extractionDirectoryPath))
                    Directory.Delete(extractionDirectoryPath, true);
                if (Directory.Exists(installDirectoryPath))
                    Directory.Delete(installDirectoryPath, true);
            }
        }

        private static void ExtractExperimentalRendererArchive(
            string archivePath,
            string stagingDirectoryPath,
            CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(stagingDirectoryPath);
            cancellationToken.ThrowIfCancellationRequested();

            if (archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            {
                using FileStream archiveStream = File.OpenRead(archivePath);
                using GZipStream gzipStream = new(archiveStream, CompressionMode.Decompress);
                TarFile.ExtractToDirectory(gzipStream, stagingDirectoryPath, overwriteFiles: true);
            }
            else
            {
                ZipFile.ExtractToDirectory(archivePath, stagingDirectoryPath, overwriteFiles: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
        }

        private static void ReplaceExperimentalRendererDirectory(string stagedDirectoryPath)
        {
            string targetDirectoryPath = ExperimentalRenderer.ExperimentalRendererDirectoryPath;
            string backupDirectoryPath = targetDirectoryPath + $".backup-{Guid.NewGuid():N}";
            bool movedExistingDirectory = false;

            try
            {
                if (Directory.Exists(targetDirectoryPath))
                {
                    Directory.Move(targetDirectoryPath, backupDirectoryPath);
                    movedExistingDirectory = true;
                }

                try
                {
                    Directory.Move(stagedDirectoryPath, targetDirectoryPath);
                }
                catch (IOException)
                {
                    // Temp and application directories can be on different
                    // volumes. Copying is the portable fallback.
                    CopyDirectoryContents(stagedDirectoryPath, targetDirectoryPath);
                    Directory.Delete(stagedDirectoryPath, true);
                }

                if (movedExistingDirectory && Directory.Exists(backupDirectoryPath))
                    Directory.Delete(backupDirectoryPath, true);
            }
            catch
            {
                if (Directory.Exists(targetDirectoryPath))
                    Directory.Delete(targetDirectoryPath, true);

                if (movedExistingDirectory && Directory.Exists(backupDirectoryPath))
                    Directory.Move(backupDirectoryPath, targetDirectoryPath);

                throw;
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

            foreach (string directoryPath in Directory.EnumerateDirectories(sourceRoot, "*",
                         SearchOption.AllDirectories))
            {
                string relativePath = Path.GetRelativePath(sourceRoot, directoryPath);
                if (relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Any(p => p == "." || p == ".."))
                    throw new InvalidDataException(
                        $"Experimental renderer archive directory escapes the staging directory: {directoryPath}");

                Directory.CreateDirectory(Path.Combine(destinationRoot, relativePath));
            }

            foreach (string filePath in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
            {
                string relativePath = Path.GetRelativePath(sourceRoot, filePath);
                if (relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Any(p => p == "." || p == ".."))
                    throw new InvalidDataException(
                        $"Experimental renderer archive file escapes the staging directory: {filePath}");

                string destinationPath = Path.Combine(destinationRoot, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                File.Copy(filePath, destinationPath, overwrite: true);
            }
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
            SetExecutableModeIfExists(
                Path.Combine(ExperimentalRenderer.ExperimentalRendererDirectoryPath, "osu-replay-viewer"),
                executableMode);
            SetExecutableModeIfExists(
                Path.Combine(ExperimentalRenderer.ExperimentalRendererDirectoryPath, "ffmpeg", "ffmpeg"),
                executableMode);
            SetExecutableModeIfExists(
                Path.Combine(ExperimentalRenderer.ExperimentalRendererDirectoryPath, "ffmpeg", "ffprobe"),
                executableMode);
            SetExecutableModeIfExists(
                Path.Combine(ExperimentalRenderer.ExperimentalRendererDirectoryPath, "ffmpeg", "ffplay"),
                executableMode);
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
            SetExecutableModeIfExists(Path.Combine(DanserGo.DanserGoDirectoryPath, "ffmpeg", "ffprobe"),
                executableMode);
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
