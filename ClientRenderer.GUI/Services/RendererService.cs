using ClientRenderer.Abstractions;
using ClientRenderer.Connection;
using ClientRenderer.Logging;
using ClientRenderer.Render;
using ClientRenderer.RenderPipeline;
using ClientRenderer.Startup;
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
    public sealed class RendererService
    {
        public static RendererService Instance => _instance.Value;
        private static readonly Lazy<RendererService> _instance = new(() => new RendererService());
        private readonly object _sync = new();
        private Task? _runTask;

        public Task RunTask(string encoder, string serverUrl)
        {
            lock (_sync)
            {
                _runTask ??= Task.Run(() => RunAsync(encoder, serverUrl));
                return _runTask;
            }
        }

        private async Task RunAsync(string encoder, string serverUrl)
        {
            try
            {
                using var bootstrapServices = new ServiceCollection()
                    .AddSingleton<IConfigurationLoader, ConfigurationLoader>()
                    .BuildServiceProvider();

                var appConfig = await bootstrapServices.GetRequiredService<IConfigurationLoader>().LoadAsync();
                var cts = new CancellationTokenSource();
                var cancellationToken = cts.Token;

                ValidateRenderingDependencies(appConfig.OsuApiV2Configuration.ClientId, appConfig.OsuApiV2Configuration.ClientSecret);

                Logger.Log($"{encoder} has been set as a default danser encoder.");

                await using var runtimeServices = new ServiceCollection()
                    .AddSingleton(appConfig)
                    .AddSingleton(new BanchoApiV2(appConfig.OsuApiV2Configuration.ClientId, appConfig.OsuApiV2Configuration.ClientSecret))
                    .AddSingleton<IServerConnection>(_ => new ServerConnection(serverUrl, appConfig.RendererCredentials, cancellationToken))
                    .AddSingleton<IReplaysDownloader, ReplaysDownloader>()
                    .AddSingleton<IBeatmapsetsDownloader>(sp => new BeatmapsetsDownloader(sp.GetRequiredService<BanchoApiV2>(), appConfig.OsuSessionCookie))
                    .AddSingleton<ISkinsDownloader, SkinsDownloader>()
                    .AddSingleton<IThumbnailRenderer, ThumbnailRenderer>()
                    .AddSingleton<IVideoRenderer, VideoRenderer>()
                    .AddSingleton<IRenderWorker>(sp => new RenderWorker(sp.GetRequiredService<IVideoRenderer>(), sp.GetRequiredService<IServerConnection>(), encoder))
                    .BuildServiceProvider();

                Logger.Log("osu! api v2 credentials loaded. They will be validated on the first real API request.");

                var serverConnection = runtimeServices.GetRequiredService<IServerConnection>();
                while (!await serverConnection.InitializeToken() && !cancellationToken.IsCancellationRequested)
                {
                    Logger.LogWarning("Failed to initialize a token, retrying in 5 seconds... Check your renderer-settings.json (or internet connection)");
                    await Task.Delay(5000, cancellationToken);
                }

                Logger.Log("Token was successfully initialized");
                await runtimeServices.GetRequiredService<IRenderWorker>().RunAsync(cancellationToken);
            }
            catch (InvalidOperationException e)
            {
                Logger.LogError(e.Message);
                if (Environment.UserInteractive)
                    Console.ReadKey();
            }
            catch (Exception e)
            {
                Logger.LogError(e.ToString());
                File.WriteAllText("error.txt", $"Crash: {e}");
            }
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

            DanserGo.CreateDirectoriesIfNeeded();
        }
    }
}
