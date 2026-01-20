using ClientRenderer.Connection;
using ClientRenderer.Models;
using ClientRenderer.Render;
using CommandLine;
using DanserWrapper;
using ExperimentalRendererWrapper;
using OsuApi.BanchoV2;
using OsuParsers.Replays;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
            Log($"Using the following server: {o.ServerUrl}");
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
    LogError($"Error. Specify your osu_session cookie at {cookieFile}");
    Console.ReadKey();
    return;
}
else
{
    Log($"Checking your osu_session cookie...");
    using var httpClient = new HttpClient();
    using var request = new HttpRequestMessage(HttpMethod.Head, "https://osu.ppy.sh/beatmapsets/41823/download");
    request.Headers.Add("Cookie", $"osu_session={File.ReadAllText(cookieFile)}");
    request.Headers.Referrer = new Uri("https://osu.ppy.sh/beatmapsets/41823/download");
    var response = await httpClient.SendAsync(request);
    if (!response.IsSuccessStatusCode)
    {
        LogError($"Error. Renew your token. You can try to log out and log in into your osu! profile on website.");
        Console.ReadKey();
        return;
    }
    Log($"Your osu_session cookie is OK.");
}
string osuSessionCookie = File.ReadAllText(cookieFile);
string osuApiConfigFilePath = Path.Combine(settingsDirectory, "osu-api.json");
if (!File.Exists(osuApiConfigFilePath))
{
    File.WriteAllText(osuApiConfigFilePath, JsonSerializer.Serialize(new OsuApiV2Configuration(), intended));
    LogError($"Error. Specify your osu api v2 credentials at {osuApiConfigFilePath}");
    Console.ReadKey();
    return;
}

var osuApiConfig = JsonSerializer.Deserialize<OsuApiV2Configuration>(File.ReadAllText(osuApiConfigFilePath))!;
BanchoApiV2 osuApi;
try
{
    osuApi = new BanchoApiV2(osuApiConfig.ClientId, osuApiConfig.ClientSecret);
    Log($"Your osu api v2 credentials are OK.");
}
catch (Exception ex)
{
    LogError($"Error. Incorrect osu api v2 credentials. Check your osu api v2 credentials at {osuApiConfigFilePath}");
    Console.ReadKey();
    return;
}

string rendererSettingsFilePath = Path.Combine(settingsDirectory, "renderer-settings.json");
if (!File.Exists(rendererSettingsFilePath))
{
    File.WriteAllText(rendererSettingsFilePath, JsonSerializer.Serialize(new RendererCredentials(), intended));
    LogError($"Error. Specify your renderer settings at {rendererSettingsFilePath}. If you don't have it, contact Shoukko");
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
Log($"{chosenEncoder} has been set as a default danser encoder.");

// Load dict
LoadDictionary();

// Setup danser 
DanserGo.AdjustOsuApiCredentials(osuApiConfig.ClientId, osuApiConfig.ClientSecret);
DanserGo.AdjustDanserGoPath(Environment.OSVersion);
if (!DanserGo.DanserExists())
{
    LogError("Danser-go does not exist!");
    return;
}

// Setup experimental renderer
ExperimentalRenderer.AdjustExperimentalRendererPath(Environment.OSVersion);
if (!ExperimentalRenderer.ExperimentalRendererExists())
{
    LogError("Experimental renderer does not exist!");
    return;
}

// Setup danser directories and load existing beatmaps
DanserGo.CreateDirectoriesIfNeeded();
ReplaysService.LoadAllBeatmapsHashes();

// Connect to the server
ServerConnection serverConnection = new ServerConnection(url, rendererCredentials, cancellationToken);
while (!await serverConnection.InitializeToken() && !cancellationToken.IsCancellationRequested)
{
    LogError("Failed to initialize a token, retrying in 5 seconds... Check your renderer-settings.json (or internet connection)");
    await Task.Delay(5000);
}
Log("Token was successfully initialized");

bool useExperimentalRenderer;
BeatmapsetsService beatmapsetsService = new BeatmapsetsService();
RenderJob? renderJob = null;
while (!cancellationToken.IsCancellationRequested)
{
    try
    {
        Log("Waiting for new jobs...");
        renderJob = await serverConnection.GetNextRenderJob();
        while (renderJob is null && !cancellationToken.IsCancellationRequested)
        {
            LogError("Received a null render job, polling again in 5 seconds...");
            await Task.Delay(5000);
            renderJob = await serverConnection.GetNextRenderJob();
        }
        if (cancellationToken.IsCancellationRequested)
        {
            LogError("Closing...");
            break;
        }

        Log($"[JobId:{renderJob!.JobId}] New render job received!");
        useExperimentalRenderer = renderJob.RenderSettings.UseExperimentalRenderer;
        await RenderVideo();
    }
    catch (Exception e)
    {
        if (renderJob != null)
        {
            try
            {
                await serverConnection.Failure(renderJob.JobId, e.Message, false);
            }
            catch { }
            LogError($"[JobId:{renderJob!.JobId}] Failed.");
        }
        LogError(e.ToString());
    }
}

// END OF MAIN FUNCTION ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~

async Task<(Replay decodedReplay, byte[] replay, bool shouldReturn)> DownloadReplay()
{
    await serverConnection.ReportRenderingProgress(renderJob!.JobId, -2);
    Log($"[JobId:{renderJob!.JobId}] Downloading a replay...");
    var replay = await serverConnection.DownloadReplay(renderJob!.JobId);
    var decodedReplay = ReplaysService.DecodeReplay(replay);
    renderJob.PlayerName = decodedReplay.PlayerName;
    bool shouldReturn = false;
    if (decodedReplay.Ruleset != OsuParsers.Enums.Ruleset.Standard)
    {
        useExperimentalRenderer = true;
    }

    return (decodedReplay, replay, shouldReturn);
}

async Task<(string replayPath, string beatmapHash, bool shouldReturn, string oszPath)> DownloadBeatmapset(Replay decodedReplay, byte[] replay)
{
    await serverConnection.ReportRenderingProgress(renderJob!.JobId, -1);
    Log($"[JobId:{renderJob!.JobId}] Downloading a beatmap...");
    var beatmapHash = decodedReplay.BeatmapMD5Hash;

    string oszFileName = $"{beatmapHash}.osz";
    string oszPath = Path.Combine(AppContext.BaseDirectory, oszFileName);
    string beatmapsetDirectoryPath = Path.Combine(DanserGo.SongsPath, oszFileName);
    ReplaysService.LoadAllBeatmapsHashes();
    if (!ReplaysService.BeatmapExists(beatmapHash))
    {
        Log($"[JobId:{renderJob!.JobId}] The requested beatmap does not exist!");
        Log($"[JobId:{renderJob!.JobId}] Downloading beatmapset...");

        var downloadResult = await beatmapsetsService.DownloadBeatmapset(beatmapHash);

        if (downloadResult.Success)
        {
            using (var oszStream = downloadResult.Output!)
            {
                var oszStreamCopy = new MemoryStream();
                await oszStream.CopyToAsync(oszStreamCopy);
                oszStreamCopy.Position = 0;

                if (useExperimentalRenderer)
                {
                    using var fs = new FileStream(oszPath, FileMode.Create, FileAccess.Write);
                    await oszStreamCopy.CopyToAsync(fs);
                    oszStreamCopy.Position = 0;
                }
                ZipFile.ExtractToDirectory(oszStreamCopy, beatmapsetDirectoryPath);
            }
        }

        ReplaysService.LoadAllBeatmapsHashes();
        if (!ReplaysService.BeatmapExists(beatmapHash) || !downloadResult.Success)
        {
            Directory.Delete(beatmapsetDirectoryPath, true);
            Log($"[JobId:{renderJob!.JobId}] Downloading beatmapset via osu");
            downloadResult = await beatmapsetsService.DownloadBeatmapViaOsu(BeatmapsetsService.HashToValues[beatmapHash].BeatmapsetId, osuSessionCookie);
            if (!downloadResult.Success)
            {
                await serverConnection.Failure(renderJob.JobId, "beatmapset_download_failed", false);
                LogError($"[JobId:{renderJob!.JobId}] Failed to download a beatmapset!");
                LogError($"Error. Your osu_session cookie is probably expired. Renew it. Error message: {downloadResult.Exception!.Message}");
                return (string.Empty, beatmapHash, true, oszPath);
            }

            using (var oszStream = downloadResult.Output!)
            {
                var oszStreamCopy = new MemoryStream();
                await oszStream.CopyToAsync(oszStreamCopy);
                oszStreamCopy.Position = 0;

                if (useExperimentalRenderer)
                {
                    using var fs = new FileStream(oszPath, FileMode.Create, FileAccess.Write);
                    await oszStreamCopy.CopyToAsync(fs);
                    oszStreamCopy.Position = 0;
                }
                ZipFile.ExtractToDirectory(oszStreamCopy, beatmapsetDirectoryPath);
            }
        }
        Log($"[JobId:{renderJob!.JobId}] Sucessfully downloaded beatmapset! (.osz)");
    }
    else
    {
        if (useExperimentalRenderer)
        {
            if (File.Exists(oszPath))
            {
                File.Delete(oszPath);
            }
            ZipFile.CreateFromDirectory(beatmapsetDirectoryPath, oszPath);
        }
        Log($"[JobId:{renderJob!.JobId}] Beatmap exists locally, proceeding to render...");
    }
    string replayPath = Path.GetFullPath(beatmapHash + ".osr");
    await File.WriteAllBytesAsync(replayPath, replay, cancellationToken);

    SaveDictionary();

    return (replayPath, beatmapHash, false, oszPath);
}

async Task<string> DownloadSkin()
{
    string oskPath = Path.Combine(AppContext.BaseDirectory, renderJob.RenderSettings.SkinName);
    renderJob.RenderSettings.Encoder = chosenEncoder;
    if (renderJob.RenderSettings.SkinName.EndsWith(".osk"))
    {
        string skinNameNoOsk = renderJob.RenderSettings.SkinName[..^4].GetHashCode().ToString();
        string skinDirectory = Path.Combine(DanserGo.DanserGoDirectoryPath, "skins", skinNameNoOsk);
        if (!Directory.Exists(skinDirectory))
        {
            string skinNameHex = Convert.ToHexString(Encoding.ASCII.GetBytes(renderJob.RenderSettings.SkinName)) + ".osk";
            Log($"[JobId:{renderJob!.JobId}] Skin: {renderJob.RenderSettings.SkinName}. Downloading a skin...");
            Stream skinAsStream = new MemoryStream(await serverConnection.DownloadSkin(skinNameHex));
            if (useExperimentalRenderer)
            {
                using var fs = new FileStream(oskPath, FileMode.Create, FileAccess.Write);
                await skinAsStream.CopyToAsync(fs);
                skinAsStream.Position = 0;
            }
            ZipFile.ExtractToDirectory(skinAsStream, skinDirectory);
        }
        else
        {
            if (useExperimentalRenderer)
            {
                if (File.Exists(oskPath))
                {
                    File.Delete(oskPath);
                }
                ZipFile.CreateFromDirectory(skinDirectory, oskPath);
            }
            Log($"[JobId:{renderJob!.JobId}] Skin: {renderJob.RenderSettings.SkinName}. Already exists.");
        }
        renderJob.RenderSettings.SkinName = skinNameNoOsk;
    }

    DanserGo.AdjustConfig(renderJob.RenderSettings);
    Log($"[JobId:{renderJob!.JobId}] Start rendering");

    return oskPath;
}

async Task RenderVideo()
{
    // Download replay
    (Replay decodedReplay, byte[] replay, bool shouldReturn) = await DownloadReplay();
    if (shouldReturn) return;


    // Download beatmap
    (string replayPath, string beatmapHash, shouldReturn, string oszPath) = await DownloadBeatmapset(decodedReplay, replay);
    if (shouldReturn) return;

    int beatmapLength = 0;
    if (BeatmapsetsService.HashToValues.TryGetValue(beatmapHash, out var beatmapsetInfo))
    {
        beatmapLength = beatmapsetInfo.TotalLength;
    }


    // Download skin if needed
    string skinOskPath = await DownloadSkin();

    // Render using danser-go
    string videoPath = Path.Combine(DanserGo.VideosPath, beatmapHash + ".mp4");
    if (!useExperimentalRenderer)
    {
        DanserResult result;
        ConcurrentDictionary<string, string> renderUpdates = new();
        try
        {
            string arguments = $"-r \"{replayPath}\" " +
                              $"-out \"{beatmapHash}\" " +
                              $"-preciseprogress";
            Task<DanserResult> renderTask = new DanserGo().ExecuteAsync(arguments, renderUpdates);

            while (renderTask.IsCompleted == false && !cancellationToken.IsCancellationRequested)
            {
                if (renderUpdates.TryGetValue("Progress", out string? progressString) &&
                    double.TryParse(progressString, out double progress) && progress != 0)
                {
                    await serverConnection.ReportRenderingProgress(renderJob!.JobId, Math.Min(1.0, progress));
                    Log($"[JobId:{renderJob!.JobId}] Rendering progress: {progress * 100:0.00}%");
                }
                await Task.Delay(1000, cancellationToken);
            }

            result = await renderTask;

            // Match map name
            var mapNameRegex = new Regex(@"Playing: (.*)", RegexOptions.Compiled);
            var matchMapName = mapNameRegex.Match(result.Output + "\n" + result.Error);
            if (matchMapName.Success && !renderUpdates.ContainsKey("Map"))
            {
                renderJob.MapName = matchMapName.Groups[1].Value.Trim();
            }
        }
        catch (Exception ex)
        {
            await serverConnection.Failure(renderJob.JobId, "danser", false);
            LogError($"[JobId:{renderJob!.JobId}] Failed to render a replay! Error when calling danser-go");
            LogError(ex.ToString());
            return;
        }

        if (!result.Success)
        {
            await serverConnection.Failure(renderJob.JobId, "danser", false);
            LogError($"[JobId:{renderJob!.JobId}] Failed to render a replay! Saving danser logs");
            File.WriteAllText(Path.Combine($"danser_log{DateTime.UtcNow.ToFileTimeUtc()}"), "Danser Standard Output:\n" + result.Output + "\n\n\nDanser Error Output:\n" + result.Error);
            return;
        }
    }
    else
    {
        ExperimentalRendererResult result;
        ConcurrentDictionary<string, string> renderUpdates = new() { ["BeatmapLength"] = $"{beatmapLength}"};
        try
        {
            string arguments =
                $"--view file \"{replayPath}\" " +
                $"--import-beatmap \"{oszPath}\" " +
                $"--record " +
                $"--record-output \"{videoPath}\" " +
                $"--yes ";

            if (renderJob.RenderSettings.SkinName != "default")
            {
                arguments += $"--skin import \"{skinOskPath}\"";
            }

            var renderTask = new ExperimentalRenderer().ExecuteAsync(arguments, renderUpdates);

            while (renderTask.IsCompleted == false && !cancellationToken.IsCancellationRequested)
            {
                if (renderUpdates.TryGetValue("Progress", out string? progressString) &&
                    double.TryParse(progressString, out double progress) && progress != 0)
                {
                    await serverConnection.ReportRenderingProgress(renderJob!.JobId, Math.Min(1.0, progress));
                    Log($"[JobId:{renderJob!.JobId}] Rendering progress: {progress * 100:0.00}%");
                }
                await Task.Delay(1000, cancellationToken);
            }

            result = await renderTask;
        }
        catch (Exception ex)
        {
            await serverConnection.Failure(renderJob.JobId, "Failed to render a replay using experimental renderer", false);
            LogError($"[JobId:{renderJob!.JobId}] Failed to render replay! Error when calling experimental renderer");
            LogError(ex.ToString());
            return;
        }

        if (!result.Success)
        {
            await serverConnection.Failure(renderJob.JobId, "Failed to render a replay using experimental renderer. Result is not successful", false);
            LogError($"[JobId:{renderJob!.JobId}] Failed to render replay! Saving danser logs");
            File.WriteAllText(Path.Combine($"experimental-renderer_log{DateTime.UtcNow.ToFileTimeUtc()}"), "Experimental Renderer Standard Output:\n" + result.Output + "\n\n\nExperimental Renderer Error Output:\n" + result.Error);
            return;
        }
    }

    Log($"[JobId:{renderJob!.JobId}] Rendering done!");
    Log($"[JobId:{renderJob!.JobId}] Uploading to the server...!");
    bool successfullyUploaded = false;
    while (!cancellationToken.IsCancellationRequested)
    {
        try
        {
            await serverConnection.PostVideo(videoPath, renderJob.JobId);
            successfullyUploaded = true;
            break;
        }
        catch (Exception ex)
        {
            LogError($"[JobId:{renderJob!.JobId}] Failed to upload a replay: {ex.Message}. Retrying...");
            await Task.Delay(2000); // wait before retrying
        }
    }

    if (!successfullyUploaded)
    {
        await serverConnection.Failure(renderJob.JobId, "video_upload_failed", true);
        LogError($"[JobId:{renderJob!.JobId}] Error while uploading a replay video file");
        return;
    }
    Log($"[JobId:{renderJob!.JobId}] Successfully uploaded");
    if (decodedReplay.Ruleset is OsuParsers.Enums.Ruleset.Standard)
    {
        await RenderThumbnail(beatmapLength, replayPath, beatmapHash);
    }
    else
    {
        Log("A thumbnail will not be rendered - the replay is not from osu!std");
    }

    try
    {
        await serverConnection.SetRenderJobMetadata(renderJob.JobId, renderJob);
    }
    catch (Exception ex)
    {
        LogError($"[JobId:{renderJob!.JobId}] Failed to set render job metadata! Skipping...");
        LogError(ex.ToString());
    }

    await serverConnection.FinishRendering(renderJob.JobId);
    Log($"[JobId:{renderJob!.JobId}] Rendering finished");
}

async Task RenderThumbnail(int videoLength, string replayPath, string beatmapHash)
{
    Log($"[JobId:{renderJob!.JobId}] Generating a thumbnail...");
    DanserResult result;
    ConcurrentDictionary<string, string> renderUpdates = new();
    try
    {
        string arguments = $"-r \"{replayPath}\" " +
                           $"-out \"{beatmapHash}\" " +
                           $"-ss \"{videoLength + 6}\"";

        result = await new DanserGo().ExecuteAsync(arguments, new());

        if (!result.Success)
        {
            arguments = $"-r \"{replayPath}\" " +
                           $"-out \"{beatmapHash}\" " +
                           $"-ss \"{0}\"";
            result = await new DanserGo().ExecuteAsync(arguments, new());
        }
        Log($"[JobId:{renderJob!.JobId}] Successfully rendered a thumbnail!");

        Log($"[JobId:{renderJob!.JobId}] Uploading the thumbnail...");
        await serverConnection.UploadThumbnail(Path.Combine(DanserGo.ScreenshotsPath, $"{beatmapHash}.png"), renderJob.JobId);
        Log($"[JobId:{renderJob!.JobId}] The thumbnail was successfully uploaded!");
    }
    catch (Exception ex)
    {
        LogError($"[JobId:{renderJob!.JobId}] Failed to render/upload a thumbnail! Skipping...");
        LogError(ex.ToString());
        return;
    }
}

void SaveDictionary()
{
    try
    {

        if (BeatmapsetsService.HashToValues.Count == 0) return;

        Directory.CreateDirectory("data");
        var json = JsonSerializer.Serialize(BeatmapsetsService.HashToValues);
        var path = Path.Combine(AppContext.BaseDirectory, "data", "hashes.json");
        File.WriteAllText(path, json);
    }
    catch (Exception e)
    {
        LogError($"Failed to save HashToValues dict: {e}");
    }
}

void LoadDictionary()
{
    try
    {
        var path = Path.Combine(AppContext.BaseDirectory, "data", "hashes.json");
        if (!File.Exists(path)) return;

        BeatmapsetsService.HashToValues = JsonSerializer.Deserialize<ConcurrentDictionary<string, BeatmapsetsService.BeatmapsetInfo>>(File.ReadAllText(path)) ?? new();
    }
    catch (Exception e)
    {
        LogError($"Failed to save HashToValues dict: {e}");
    }
}

void Log(string message)
{
    Console.WriteLine($"\x1b[32m[{DateTime.Now:yyyy-MM-dd HH:mm:ss.ffff}] \x1b[37m{message}\x1b[0m");
}

void LogError(string message)
{
    Console.WriteLine($"\x1b[32m[{DateTime.Now:yyyy-MM-dd HH:mm:ss.ffff}] \u001b[31m{message}\x1b[0m");
}

async Task CheckForUpdatesAsync()
{
    Log("Searching for updates...");
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

        Log($"Found new update: {newVersion.TargetFullRelease.Version}");
        await mgr.DownloadUpdatesAsync(newVersion);
        Log($"Update downloaded, restarting...");
        mgr.ApplyUpdatesAndRestart(newVersion, args);
    }
    catch (Exception ex)
    {
        Log($"Failed to check for updates: {ex.Message}");
    }
}

class CommandLineOptions
{
    [Option('s', "server", Required = true, HelpText = "Set the upload server. Example: http://localhost:5000")]
    public string? ServerUrl { get; set; }

    [Option('e', "encoder", Required = false, HelpText = "Set the video encoder. Available: h264_nvenc, av1_nvenc or libx264 for cpu encoding. Defaults to h264_nvenc")]
    public string Encoder { get; set; } = "h264_nvenc";
}