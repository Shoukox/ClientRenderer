using ClientRenderer.Connection;
using ClientRenderer.Models;
using ClientRenderer.Render;
using ClientRenderer.Utils;
using CommandLine;
using DanserWrapper;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Velopack;

var cmdParserResult = Parser.Default
    .ParseArguments<CommandLineOptions>(args)
    .WithParsed(o =>
    {
        if (o.ServerUrl != null)
        {
            Console.WriteLine($"Using the following server: {o.ServerUrl}");
        }
    });
if (cmdParserResult.Tag == ParserResultType.NotParsed)
{
    Console.ReadKey();
    return;
}

await CheckForUpdatesAsync();

string url = cmdParserResult.Value.ServerUrl!;
Uri serverUri = new Uri(url);

string chosenEncoder = "h264_nvenc"; // default nvenc
if (args.Length == 4 && args[3] is "libx264")
{
    chosenEncoder = args[3];
}

DanserGo.AdjustDanserGoPath(Environment.OSVersion);
if (!DanserGo.DanserExists())
{
    Log("Danser-go does not exist!");
    return;
}
DanserGo.AdjustConfig(chosenEncoder);
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

        await serverConnection.ReportRenderingProgress(renderJob!.JobId, -2);
        Log($"[JobId:{renderJob!.JobId}] Downloading a replay...");

        // Download replay
        var replay = await serverConnection.DownloadReplay(renderJob!.JobId);
        var decodedReplay = ReplaysService.DecodeReplay(replay);
        if (decodedReplay.Ruleset != OsuParsers.Enums.Ruleset.Standard)
        {
            Log($"[JobId:{renderJob!.JobId}] Unsupported ruleset: {decodedReplay.Ruleset}. Only osu!standard is supported.");
            await serverConnection.Failure(renderJob.JobId, "ruleset", false);
            continue;
        }

        await serverConnection.ReportRenderingProgress(renderJob!.JobId, -1);
        Log($"[JobId:{renderJob!.JobId}] Downloading a beatmap...");

        // Download beatmap
        var beatmapHash = decodedReplay.BeatmapMD5Hash;
        if (!ReplaysService.BeatmapExists(beatmapHash))
        {
            Log($"[JobId:{renderJob!.JobId}] The requested beatmap does not exist!");
            // todo:
            // use official osu website
            // curl -G -H "Cookie: osu_session=sessionid" -H "Referer: https://osu.ppy.sh/beatmapsets/<beatmapsetid>" https://osu.ppy.sh/beatmapsets/<beatmapsetid>/download
            // get beatmapsetId from a hash using osu!api v1 get_beatmaps
            int beatmapsetId = await beatmapsetsService.GetBeatmapsetId(beatmapHash);
            Log($"[JobId:{renderJob!.JobId}] Downloading beatmapset {beatmapsetId}...");

            var downloadResult = await beatmapsetsService.DownloadBeatmapset(beatmapsetId);
            if (!downloadResult.Success)
            {
                await serverConnection.Failure(renderJob.JobId, "beatmapset_download_failed", false);
                Log($"[JobId:{renderJob!.JobId}] Failed to download a beatmapset!");
                Log(downloadResult.Exception!.ToString());
                continue;
            }

            Stream oszStream = downloadResult.Output!;
            using var fileStream = File.OpenWrite(Path.Combine(DanserGo.SongsPath, $"{beatmapHash}.osz"));

            await oszStream.CopyToAsync(fileStream, cancellationToken);
            await Task.Run(ReplaysService.LoadAllBeatmapsHashes, cancellationToken);
            Log($"[JobId:{renderJob!.JobId}] Sucessfully downloaded beatmapset! (.osz)");
        }
        else
        {
            Log($"[JobId:{renderJob!.JobId}] Beatmap exists locally, proceeding to render...");
        }

        Log($"[JobId:{renderJob!.JobId}] Start rendering");

        string replayPath = Path.GetFullPath(beatmapHash + ".osr");
        await File.WriteAllBytesAsync(replayPath, replay, cancellationToken);

        // Download skin if needed
        string skinName = renderJob.RenderSkin.Substring(0, renderJob.RenderSkin.Length - 4);
        DanserGo.AdjustConfig(chosenEncoder, skinName);
        if (renderJob.RenderSkin != "default")
        {
            string skinNameHex = Convert.ToHexString(Encoding.ASCII.GetBytes(renderJob.RenderSkin)) + ".osk";
            string skinDirectory = Path.Combine(DanserGo.DanserGoDirectoryPath, "skins", skinName);
            if (!Directory.Exists(skinDirectory))
            {
                Stream skinAsStream = new MemoryStream(await serverConnection.DownloadSkin(skinNameHex));
                ZipFile.ExtractToDirectory(skinAsStream, skinDirectory);
            }
        }


        // Render using danser-go
        DanserResult result;
        ConcurrentDictionary<string, string> renderUpdates = new();
        try
        {
            Task<DanserResult> renderTask = new DanserGo()
                .ExecuteAsync($"-r \"{replayPath}\" " +
                              $"-out \"{beatmapHash}\"", renderUpdates);

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
            continue;
        }

        if (!result.Success)
        {
            await serverConnection.Failure(renderJob.JobId, "danser", false);
            Log($"[JobId:{renderJob!.JobId}] Failed to render replay! Saving danser logs");
            File.WriteAllText(Path.Combine($"danser_log{DateTime.UtcNow.ToFileTimeUtc()}"), result.ExitCode == 0 ? result.Output : result.Error);
            continue;
        }

        Log($"[JobId:{renderJob!.JobId}] Rendering done!");
        Log($"[JobId:{renderJob!.JobId}] Uploading to the server...!");

        bool successfullyUploaded = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                string videoPath = Path.Combine(DanserGo.VideosPath, beatmapHash + ".mp4");
                await serverConnection.PostVideoAsync(videoPath, renderJob.JobId);
                successfullyUploaded = true;
                break;
            }
            catch (Exception ex)
            {
                Log($"[JobId:{renderJob!.JobId}] Failed to upload replay: {ex.Message}. Retrying...");
                await Task.Delay(2000); // wait before retrying
            }
        }

        if (!successfullyUploaded)
        {
            await serverConnection.Failure(renderJob.JobId, "video_upload_failed", true);
            Log($"[JobId:{renderJob!.JobId}] Error while uploading a replay video file");
            continue;
        }
        Log($"[JobId:{renderJob!.JobId}] Successfully uploaded");

        await serverConnection.FinishRendering(renderJob.JobId);
        Log($"[JobId:{renderJob!.JobId}] Rendering finished");
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

void Log(string message)
{
    Console.WriteLine($"\x1b[32m[{DateTime.Now:u}] \x1b[37m{message}\x1b[0m");
}

async Task CheckForUpdatesAsync()
{
    Log("Searching for updates...");
    try
    {
        var mgr = new UpdateManager("https://the.place/you-host/updates");

        var newVersion = await mgr.CheckForUpdatesAsync();
        if (newVersion == null)
            return;

        await mgr.DownloadUpdatesAsync(newVersion);

        mgr.ApplyUpdatesAndRestart(newVersion);
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