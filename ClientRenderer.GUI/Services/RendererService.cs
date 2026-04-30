using ClientRenderer.Abstractions;
using ClientRenderer.Connection;
using ClientRenderer.Logging;
using ClientRenderer.Render;
using ClientRenderer.RenderPipeline;
using ClientRenderer.Startup;
using ClientRenderer.Helpers;
using DanserWrapper;
using ExperimentalRendererWrapper;
using Microsoft.Extensions.DependencyInjection;
using OsuApi.BanchoV2;
using System;
using System.IO;
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
                _ = RunTask(encoder, serverUrl);

                if (waitForOnline)
                {
                    while(_state is not RendererServiceState.Online and not RendererServiceState.Failed)
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

                ValidateRenderingDependencies(appConfig.OsuApiV2Configuration.ClientId, appConfig.OsuApiV2Configuration.ClientSecret);

                Logger.Log($"{encoder} has been set as a default danser encoder.");

                var serverConnection = new ServerConnection(serverUrl, appConfig.RendererCredentials, cancellationToken);
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
                    .AddSingleton<IRenderWorker>(sp => new RenderWorker(sp.GetRequiredService<IVideoRenderer>(), sp.GetRequiredService<IServerConnection>(), encoder))
                    .BuildServiceProvider();

                Logger.Log("osu! api v2 credentials loaded. They will be validated on the first real API request.");

                while (!await serverConnection.InitializeToken() && !cancellationToken.IsCancellationRequested)
                {
                    Logger.LogWarning("Failed to initialize a token, retrying in 5 seconds... Check your renderer-settings.json (or internet connection)");
                    await Task.Delay(5000, cancellationToken);
                }

                if (!cancellationToken.IsCancellationRequested)
                    await runtimeServices.GetRequiredService<IRenderWorker>().RunAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Logger.Log("Renderer service was cancelled.");
            }
            catch (InvalidOperationException e)
            {
                Logger.LogError(e.Message);
                setStatus(RendererServiceState.Failed, 0);
            }
            catch (Exception e)
            {
                Logger.LogError(e.ToString());
                File.WriteAllText("error.txt", $"Crash: {e}");
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

                if(_state != RendererServiceState.Failed)
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

            if (runTask != null)
            {
                try
                {
                    await runTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (AggregateException ex) when (ex.InnerExceptions.Count == 1 && ex.InnerException is OperationCanceledException)
                {
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

        private static void ValidateRenderingDependencies(int osuClientId, string osuClientSecret)
        {
            DanserGo.AdjustOsuApiCredentials(osuClientId, osuClientSecret);
            DanserGo.AdjustDanserGoPath(Environment.OSVersion);
            if (!DanserGo.DanserExists())
                throw new InvalidOperationException("Danser-go does not exist!");

            ExperimentalRenderer.AdjustExperimentalRendererPath(Environment.OSVersion);
            if (!ExperimentalRenderer.ExperimentalRendererExists())
                throw new InvalidOperationException("Experimental renderer does not exist!");

            WindowsGpuPreferenceHelper.SetHighPerformanceForExecutables([
                Path.Combine(DanserGo.DanserGoDirectoryPath, "ffmpeg", "ffmpeg.exe"),
                Path.Combine(DanserGo.DanserGoDirectoryPath, "ffmpeg", "ffprobe.exe"),
                Path.Combine(ExperimentalRenderer.ExperimentalRendererDirectoryPath, "ffmpeg", "ffmpeg.exe"),
                Path.Combine(ExperimentalRenderer.ExperimentalRendererDirectoryPath, "ffmpeg", "ffprobe.exe"),
                Path.Combine(ExperimentalRenderer.ExperimentalRendererDirectoryPath, "ffmpeg", "ffplay.exe")
            ]);

            DanserGo.CreateDirectoriesIfNeeded();
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
