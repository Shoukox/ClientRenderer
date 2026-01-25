using ClientRenderer.Connection;
using ClientRenderer.Logging;
using ClientRenderer.Models;
using ClientRenderer.RenderPipeline;
using CommandLine;
using DanserWrapper;
using ExperimentalRendererWrapper;
using OsuApi.BanchoV2;
using System.Text.Json;
using Velopack;
using Velopack.Sources;

// Velopack auto updater
VelopackApp.Build().Run();
await CheckForUpdatesAsync();

// Cmd parser
var cmdParserResult = Parser.Default
    .ParseArguments<CommandLineOptions>(args)
    .WithParsed(o =>
    {
        if (o.ServerUrl != null)
        {
            Logger.Log($"Using the following server: {o.ServerUrl}");
        }
    });
if (cmdParserResult.Tag == ParserResultType.NotParsed)
{
    Console.ReadKey();
    return;
}

var intended = new JsonSerializerOptions() { WriteIndented = true };

#region LoadConfigFiles
string settingsDirectory = Path.Combine(AppContext.BaseDirectory, "settings");
Directory.CreateDirectory(settingsDirectory);

string cookieFile = Path.Combine(settingsDirectory, "cookie.txt");
if (!File.Exists(cookieFile))
{
    File.WriteAllText(cookieFile, "INSERT YOUR OSU-SESSION COOKIE HERE");
    Logger.LogError($"Error. Specify your osu_session cookie at {cookieFile}");
    Console.ReadKey();
    return;
}
else
{
    Logger.Log($"Checking your osu_session cookie...");
    using var httpClient = new HttpClient();
    using var request = new HttpRequestMessage(HttpMethod.Head, "https://osu.ppy.sh/beatmapsets/41823/download");
    request.Headers.Add("Cookie", $"osu_session={File.ReadAllText(cookieFile)}");
    request.Headers.Referrer = new Uri("https://osu.ppy.sh/beatmapsets/41823/download");
    var response = await httpClient.SendAsync(request);
    if (!response.IsSuccessStatusCode)
    {
        Logger.LogError($"Error. Renew your token. You can try to Logger.Log out and Logger.Log in into your osu! profile on website.");
        Console.ReadKey();
        return;
    }
    Logger.Log($"Your osu_session cookie is OK.");
}
string osuSessionCookie = File.ReadAllText(cookieFile);
string osuApiConfigFilePath = Path.Combine(settingsDirectory, "osu-api.json");
if (!File.Exists(osuApiConfigFilePath))
{
    File.WriteAllText(osuApiConfigFilePath, JsonSerializer.Serialize(new OsuApiV2Configuration(), intended));
    Logger.LogError($"Error. Specify your osu api v2 credentials at {osuApiConfigFilePath}");
    Console.ReadKey();
    return;
}

var osuApiConfig = JsonSerializer.Deserialize<OsuApiV2Configuration>(File.ReadAllText(osuApiConfigFilePath))!;
BanchoApiV2 osuApi;
try
{
    osuApi = new BanchoApiV2(osuApiConfig.ClientId, osuApiConfig.ClientSecret);
    Logger.Log($"Your osu api v2 credentials are OK.");
}
catch (Exception ex)
{
    Logger.LogError($"Error. Incorrect osu api v2 credentials. Check your osu api v2 credentials at {osuApiConfigFilePath}");
    Console.ReadKey();
    return;
}

string rendererSettingsFilePath = Path.Combine(settingsDirectory, "renderer-settings.json");
if (!File.Exists(rendererSettingsFilePath))
{
    File.WriteAllText(rendererSettingsFilePath, JsonSerializer.Serialize(new RendererCredentials(), intended));
    Logger.LogError($"Error. Specify your renderer settings at {rendererSettingsFilePath}. If you don't have it, contact Shoukko");
    Console.ReadKey();
    return;
}
var rendererCredentials = JsonSerializer.Deserialize<RendererCredentials>(File.ReadAllText(rendererSettingsFilePath))!;
#endregion

// Cancellation token
var cts = new CancellationTokenSource();
var cancellationToken = cts.Token;

// Set server url
string url = cmdParserResult.Value.ServerUrl!;
Uri serverUri = new Uri(url);

// Set encoder
string chosenEncoder = cmdParserResult.Value.Encoder;
Logger.Log($"{chosenEncoder} has been set as a default danser encoder.");

// Setup danser 
DanserGo.AdjustOsuApiCredentials(osuApiConfig.ClientId, osuApiConfig.ClientSecret);
DanserGo.AdjustDanserGoPath(Environment.OSVersion);
if (!DanserGo.DanserExists())
{
    Logger.LogError("Danser-go does not exist!");
    return;
}

// Setup experimental renderer
ExperimentalRenderer.AdjustExperimentalRendererPath(Environment.OSVersion);
if (!ExperimentalRenderer.ExperimentalRendererExists())
{
    Logger.LogError("Experimental renderer does not exist!");
    return;
}

// Setup danser directories and load existing beatmaps
DanserGo.CreateDirectoriesIfNeeded();

// Connect to the server
ServerConnection serverConnection = new ServerConnection(url, rendererCredentials, cancellationToken);
while (!await serverConnection.InitializeToken() && !cancellationToken.IsCancellationRequested)
{
    Logger.LogError("Failed to initialize a token, retrying in 5 seconds... Check your renderer-settings.json (or internet connection)");
    await Task.Delay(5000);
}
Logger.Log("Token was successfully initialized");

RenderPipelineInfo? info = null;
var videoRenderer = new VideoRenderer(osuApi, osuSessionCookie);
while (!cancellationToken.IsCancellationRequested)
{
    try
    {
        Logger.Log("Waiting for new jobs...");
        RenderJob? renderJob = await serverConnection.GetNextRenderJob();
        while (renderJob is null && !cancellationToken.IsCancellationRequested)
        {
            Logger.LogError("Received a null render job, polling again in 5 seconds...");
            await Task.Delay(5000);
            renderJob = await serverConnection.GetNextRenderJob();
        }
        if (cancellationToken.IsCancellationRequested)
        {
            Logger.LogError("Closing...");
            break;
        }
        Logger.Log($"[JobId:{renderJob!.JobId}] New render job received!");

        info = new RenderPipelineInfo() { RenderJob = renderJob };
        info.UseExperimentalRenderer = renderJob.RenderSettings.UseExperimentalRenderer;
        info.ChosenRenderingEncoder = chosenEncoder;
        bool success = await videoRenderer.RenderVideo(info, serverConnection, cancellationToken);
    }
    catch (Exception e)
    {
        if (info?.RenderJob != null)
        {
            try
            {
                await serverConnection.Failure(info.RenderJob.JobId, e.Message, false);
            }
            catch { }
            Logger.LogError($"[JobId:{info.RenderJob.JobId}] Failed.");
        }
        Logger.LogError(e.ToString());
    }
}

async Task CheckForUpdatesAsync()
{
    Logger.Log("Searching for updates...");
    try
    {
        var mgr = new UpdateManager(
            new GithubSource(
                repoUrl: "https://github.com/Shoukox/ClientRenderer",
                accessToken: null,
                false));

        var newVersion = await mgr.CheckForUpdatesAsync();
        if (newVersion == null)
            return;

        Logger.Log($"Found new update: {newVersion.TargetFullRelease.Version}");
        await mgr.DownloadUpdatesAsync(newVersion);
        Logger.Log($"Update downloaded, restarting...");
        mgr.ApplyUpdatesAndRestart(newVersion, args);
    }
    catch (Exception ex)
    {
        Logger.Log($"Failed to check for updates: {ex.Message}");
    }
}