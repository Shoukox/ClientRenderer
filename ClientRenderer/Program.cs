using ClientRenderer.Abstractions;
using ClientRenderer.Connection;
using ClientRenderer.Logging;
using ClientRenderer.Models;
using ClientRenderer.Render;
using ClientRenderer.RenderPipeline;
using ClientRenderer.Startup;
using CommandLine;
using DanserWrapper;
using ExperimentalRendererWrapper;
using Microsoft.Extensions.DependencyInjection;
using OsuApi.BanchoV2;
using Velopack;

try
{
    VelopackApp.Build().Run();

    using var bootstrapServices = new ServiceCollection()
        .AddSingleton<IUpdateService, UpdateService>()
        .AddSingleton<IConfigurationLoader, ConfigurationLoader>()
        .BuildServiceProvider();

    var cmdParserResult = Parser.Default
        .ParseArguments<CommandLineOptions>(args)
        .WithParsed(o => Logger.Log($"Using the following server: {o.ServerUrl}"));

    if (cmdParserResult.Tag == ParserResultType.NotParsed)
    {
        if (Environment.UserInteractive)
            Console.ReadKey();
        return;
    }

    await bootstrapServices.GetRequiredService<IUpdateService>().CheckForUpdatesAsync(args);

    var cmdOptions = cmdParserResult.Value;
    var appConfig = await bootstrapServices.GetRequiredService<IConfigurationLoader>().LoadAsync();

    var cts = new CancellationTokenSource();
    var cancellationToken = cts.Token;

    ValidateRenderingDependencies(appConfig.OsuApiV2Configuration.ClientId, appConfig.OsuApiV2Configuration.ClientSecret);

    string chosenEncoder = cmdOptions.Encoder;
    Logger.Log($"{chosenEncoder} has been set as a default danser encoder.");

    await using var runtimeServices = new ServiceCollection()
        .AddSingleton(appConfig)
        .AddSingleton(new BanchoApiV2(appConfig.OsuApiV2Configuration.ClientId, appConfig.OsuApiV2Configuration.ClientSecret))
        .AddSingleton<IServerConnection>(_ => new ServerConnection(cmdOptions.ServerUrl!, appConfig.RendererCredentials, cancellationToken))
        .AddSingleton<IReplaysDownloader, ReplaysDownloader>()
        .AddSingleton<IBeatmapsetsDownloader>(sp => new BeatmapsetsDownloader(sp.GetRequiredService<BanchoApiV2>(), appConfig.OsuSessionCookie))
        .AddSingleton<ISkinsDownloader, SkinsDownloader>()
        .AddSingleton<IThumbnailRenderer, ThumbnailRenderer>()
        .AddSingleton<IVideoRenderer, VideoRenderer>()
        .AddSingleton<IRenderWorker>(sp => new RenderWorker(sp.GetRequiredService<IVideoRenderer>(), sp.GetRequiredService<IServerConnection>(), chosenEncoder))
        .BuildServiceProvider();

    Logger.Log("osu! api v2 credentials loaded. They will be validated on the first real API request.");

    var serverConnection = runtimeServices.GetRequiredService<IServerConnection>();
    while (!await serverConnection.InitializeToken() && !cancellationToken.IsCancellationRequested)
    {
        Logger.LogError("Failed to initialize a token, retrying in 5 seconds... Check your renderer-settings.json (or internet connection)");
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

static void ValidateRenderingDependencies(int osuClientId, string osuClientSecret)
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
