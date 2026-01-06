using ClientRenderer.Connection;
using ClientRenderer.Models;
using ClientRenderer.Render;
using ClientRenderer.Utils;
using CommandLine;
using DanserWrapper;
using OsuParsers.Replays;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Velopack;
using Velopack.Sources;
using static ClientRenderer.Render.BeatmapsetsService;

VelopackApp.Build().Run();
await CheckForUpdatesAsync();

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

string url = cmdParserResult.Value.ServerUrl!;
Uri serverUri = new Uri(url);

string chosenEncoder = cmdParserResult.Value.Encoder;

DanserGo.AdjustDanserGoPath(Environment.OSVersion);
if (!DanserGo.DanserExists())
{
    Log("Danser-go does not exist!");
    return;
}

Log($"{chosenEncoder} has been set as a default danser encoder.");
DanserGo.CreateDirectoriesIfNeeded();

ConsoleExtensions.ConfigureConsoleClose(out var cancellationToken);

ReplaysService.LoadAllBeatmapsHashes();

var rendererCredentials = JsonSerializer.Deserialize<RendererCredentials>(File.ReadAllText("renderer-settings.json"))!;
ServerConnection serverConnection = new ServerConnection(url, rendererCredentials, cancellationToken);
while (!await serverConnection.InitializeToken() && !cancellationToken.IsCancellationRequested)
{
    Log("Failed to initialize a token, retrying in 5 seconds... Check your internet connection");
    await Task.Delay(5000);
}
Log("Token was successfully initialized");

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
            Log("Received a null render job, polling again in 5 seconds...");
            await Task.Delay(5000);
            renderJob = await serverConnection.GetNextRenderJob();
        }
        if (cancellationToken.IsCancellationRequested)
        {
            Log("Closing...");
            break;
        }

        Log($"[JobId:{renderJob!.JobId}] New render job received!");
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
            Log($"[JobId:{renderJob!.JobId}] Failed.");
        }
        Log(e.ToString());
    }
}

// END OF MAIN FUNCTION ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~

async Task<(Replay decodedReplay, byte[] replay, bool shouldReturn)> DownloadReplay()
{
    await serverConnection.ReportRenderingProgress(renderJob!.JobId, -2);
    Log($"[JobId:{renderJob!.JobId}] Downloading a replay...");
    var replay = await serverConnection.DownloadReplay(renderJob!.JobId);
    var decodedReplay = ReplaysService.DecodeReplay(replay);
    bool shouldReturn = false;
    if (decodedReplay.Ruleset != OsuParsers.Enums.Ruleset.Standard)
    {
        Log($"[JobId:{renderJob!.JobId}] Unsupported ruleset: {decodedReplay.Ruleset}. Only osu!standard is supported.");
        await serverConnection.Failure(renderJob.JobId, "ruleset", false);
        shouldReturn = true;
        return (decodedReplay, replay, shouldReturn);
    }

    return (decodedReplay, replay, shouldReturn);
}

async Task<(string replayPath, string beatmapHash, bool shouldReturn)> DownloadBeatmap(Replay decodedReplay, byte[] replay)
{
    await serverConnection.ReportRenderingProgress(renderJob!.JobId, -1);
    Log($"[JobId:{renderJob!.JobId}] Downloading a beatmap...");
    var beatmapHash = decodedReplay.BeatmapMD5Hash;
    bool shouldReturn = false;
    if (!ReplaysService.BeatmapExists(beatmapHash))
    {
        Log($"[JobId:{renderJob!.JobId}] The requested beatmap does not exist!");
        int? beatmapsetId = await beatmapsetsService.GetBeatmapsetId(beatmapHash);
        if (beatmapsetId == null)
        {
            await serverConnection.Failure(renderJob.JobId, "Beatmapset doesn't exist", false);
            Log($"[JobId:{renderJob!.JobId}] The given beatmapset doesn't exist on syui beatmap mirror");
            shouldReturn = true;
            return (string.Empty, beatmapHash, true);
        }
        Log($"[JobId:{renderJob!.JobId}] Downloading beatmapset {beatmapsetId}...");

        var downloadResult = await beatmapsetsService.DownloadBeatmapset(beatmapsetId.Value);
        if (!downloadResult.Success)
        {
            await serverConnection.Failure(renderJob.JobId, "beatmapset_download_failed", false);
            Log($"[JobId:{renderJob!.JobId}] Failed to download a beatmapset!");
            Log(downloadResult.Exception!.ToString());
            shouldReturn = true;
            return (string.Empty, beatmapHash, true);
        }

        Stream oszStream = downloadResult.Output!;
        using var fileStream = File.OpenWrite(Path.Combine(DanserGo.SongsPath, $"{beatmapHash}.osz"));

        await oszStream.CopyToAsync(fileStream, cancellationToken);
        ReplaysService.LoadAllBeatmapsHashes();
        Log($"[JobId:{renderJob!.JobId}] Sucessfully downloaded beatmapset! (.osz)");
    }
    else
    {
        Log($"[JobId:{renderJob!.JobId}] Beatmap exists locally, proceeding to render...");
    }
    string replayPath = Path.GetFullPath(beatmapHash + ".osr");
    await File.WriteAllBytesAsync(replayPath, replay, cancellationToken);

    return (replayPath, beatmapHash, false);
}

async Task DownloadSkin()
{
    renderJob.RenderSettings.Encoder = chosenEncoder;
    if (renderJob.RenderSettings.SkinName.EndsWith(".osk"))
    {
        string skinNameNoOsk = renderJob.RenderSettings.SkinName[..^4];
        string skinDirectory = Path.Combine(DanserGo.DanserGoDirectoryPath, "skins", skinNameNoOsk);
        if (!Directory.Exists(skinDirectory))
        {
            string skinNameHex = Convert.ToHexString(Encoding.ASCII.GetBytes(renderJob.RenderSettings.SkinName)) + ".osk";
            Log($"[JobId:{renderJob!.JobId}] Skin: {renderJob.RenderSettings.SkinName}. Downloading a skin...");
            Stream skinAsStream = new MemoryStream(await serverConnection.DownloadSkin(skinNameHex));
            ZipFile.ExtractToDirectory(skinAsStream, skinDirectory);
        }
        else
        {
            Log($"[JobId:{renderJob!.JobId}] Skin: {renderJob.RenderSettings.SkinName}. Already exists.");
        }
        renderJob.RenderSettings.SkinName = skinNameNoOsk;
    }
    DanserGo.AdjustConfig(renderJob.RenderSettings);
    Log($"[JobId:{renderJob!.JobId}] Start rendering");
}

async Task RenderVideo()
{
    // Download replay
    (Replay decodedReplay, byte[] replay, bool shouldReturn) = await DownloadReplay();
    if (shouldReturn) return;


    // Download beatmap
    (string replayPath, string beatmapHash, shouldReturn) = await DownloadBeatmap(decodedReplay, replay);
    if (shouldReturn) return;


    // Download skin if needed
    await DownloadSkin();


    // Render using danser-go
    DanserGo.DanserResult result;
    ConcurrentDictionary<string, string> renderUpdates = new();
    try
    {
        string arguments = $"-r \"{replayPath}\" " +
                          $"-out \"{beatmapHash}\"";
        Task<DanserGo.DanserResult> renderTask = new DanserGo()
            .ExecuteAsync(arguments, renderUpdates);

        while (renderTask.IsCompleted == false && !cancellationToken.IsCancellationRequested)
        {
            if (renderUpdates.TryGetValue("Progress", out string? progressString) &&
                double.TryParse(progressString, out double progress))
            {
                await serverConnection.ReportRenderingProgress(renderJob!.JobId, progress);
                Log($"[JobId:{renderJob!.JobId}] Rendering progress: {progress * 100:0.00}%");
            }
            await Task.Delay(1000, cancellationToken);
        }

        result = await renderTask;

    }
    catch (Exception ex)
    {
        await serverConnection.Failure(renderJob.JobId, "danser", false);
        Log($"[JobId:{renderJob!.JobId}] Failed to render replay! Error when calling danser-go");
        Log(ex.ToString());
        return;
    }

    if (!result.Success)
    {
        await serverConnection.Failure(renderJob.JobId, "danser", false);
        Log($"[JobId:{renderJob!.JobId}] Failed to render replay! Saving danser logs");
        File.WriteAllText(Path.Combine($"danser_log{DateTime.UtcNow.ToFileTimeUtc()}"), result.ExitCode == 0 ? result.Output : result.Error);
        return;
    }

    Log($"[JobId:{renderJob!.JobId}] Rendering done!");
    Log($"[JobId:{renderJob!.JobId}] Uploading to the server...!");

    bool successfullyUploaded = false;
    while (!cancellationToken.IsCancellationRequested)
    {
        try
        {
            string videoPath = Path.Combine(DanserGo.VideosPath, beatmapHash + ".mp4");
            await serverConnection.PostVideo(videoPath, renderJob.JobId);
            successfullyUploaded = true;
            break;
        }
        catch (Exception ex)
        {
            Log($"[JobId:{renderJob!.JobId}] Failed to upload a replay: {ex.Message}. Retrying...");
            await Task.Delay(2000); // wait before retrying
        }
    }

    if (!successfullyUploaded)
    {
        await serverConnection.Failure(renderJob.JobId, "video_upload_failed", true);
        Log($"[JobId:{renderJob!.JobId}] Error while uploading a replay video file");
        return;
    }
    Log($"[JobId:{renderJob!.JobId}] Successfully uploaded");

    await serverConnection.FinishRendering(renderJob.JobId);
    Log($"[JobId:{renderJob!.JobId}] Rendering finished");
}

void Log(string message)
{
    Console.WriteLine($"\x1b[32m[{DateTime.Now:yyyy-MM-dd HH:mm:ss.ffff}] \x1b[37m{message}\x1b[0m");
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

    [Option('e', "encoder", Required = false, HelpText = "Set the video encoder. Defaults to h264_nvenc")]
    public string Encoder { get; set; } = "h264_nvenc";
}