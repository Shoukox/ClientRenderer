using DanserWrapper;
using Microsoft.AspNetCore.SignalR.Client;

namespace ClientRenderer;

public static class ConnectionService
{
    private static SemaphoreSlim _semaphoreSlim = new SemaphoreSlim(1, 1);

    public static void SetupEventHandlers(HubConnection connection, CancellationToken token)
    {
        connection.On<string, Task>("RenderJob", async (jobMessage) =>
        {
            await _semaphoreSlim.WaitAsync(token);
            Console.WriteLine($"Got a new job! Replay name: {jobMessage}");
            Console.WriteLine($"Downloading replay...");

            string tempReplayPath;
            byte[] replayBytes;
            try
            {
                tempReplayPath = Path.GetTempFileName();
                replayBytes = await WebRequestsService.DownloadReplayAsync(jobMessage);
                await File.WriteAllBytesAsync(tempReplayPath, replayBytes, token);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to download replay: {ex.Message}");
                await IndicateRenderError(connection, jobMessage);

                _semaphoreSlim.Release();
                return;
            }

            Console.WriteLine($"Downloaded!");
            Console.WriteLine($"Checking for presence of the requested beatmap...");

            string beatmapHashFromReplay = ReplaysService.GetBeatmapMd5HashFromReplay(replayBytes);
            if (!ReplaysService.BeatmapExists(beatmapHashFromReplay))
            {
                Console.WriteLine($"The requested beatmap does not exist!");
                Console.WriteLine($"Downloading beatmapset...");

                int beatmapsetId = await OsuBeatmapsetsService.GetBeatmapsetId(beatmapHashFromReplay);
                Stream oszStream = await OsuBeatmapsetsService.DownloadBeatmapset(beatmapsetId);

                using var fileStream = File.OpenWrite(Path.Combine(DanserGo.SongsPath, $"{beatmapHashFromReplay}.osz"));
                await oszStream.CopyToAsync(fileStream, token);

                await Task.Run(ReplaysService.LoadAllBeatmapsHashes, token);

                Console.WriteLine($"Sucessfully downloaded beatmapset! (.osz)");
            }

            Console.WriteLine($"Start rendering");

            DanserResult result;
            string videoFileName;
            try
            {
                videoFileName = Path.GetFileNameWithoutExtension(jobMessage);
                result = await new DanserGo()
                    .ExecuteAsync($"-r \"{tempReplayPath}\" " +
                                  $"-out \"{videoFileName}\"");

            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed to render replay! Error when calling danser-go");
                Console.WriteLine(ex.ToString());
                await IndicateRenderError(connection, jobMessage);
                _semaphoreSlim.Release();
                return;
            }

            if (!result.Success)
            {
                Console.WriteLine($"Failed to render replay!");
                await IndicateRenderError(connection, jobMessage);
                _semaphoreSlim.Release();
                return;
            }

            Console.WriteLine($"Rendering done!");
            Console.WriteLine($"Uploading to the server...!");

            try
            {
                string videoPath = Path.Combine(DanserGo.VideosPath, videoFileName + ".mp4");
                await WebRequestsService.PostVideoAsync(videoPath, jobMessage);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to upload replay: {ex.Message}");
                await IndicateRenderError(connection, jobMessage);
                _semaphoreSlim.Release();
                return;
            }

            Console.WriteLine($"Successfully uploaded");
            _semaphoreSlim.Release();
        });

        connection.On<string>("ReceiveMessage",
            (notification) => { Console.WriteLine($"[NOTIFICATION] {notification}"); });

        connection.Closed += async (error) =>
        {
            Console.WriteLine($"Connection closed: {error?.Message ?? "No error"}");

            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(5000, token);

                    if (connection.State == HubConnectionState.Disconnected)
                    {
                        await connection.StartAsync(token);
                        Console.WriteLine("Reconnected!");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Reconnection failed: {ex.Message}");
                    Console.WriteLine($"Retrying after 5 seconds...");
                }

                try
                {
                    await Task.Delay(5000, token);
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine($"Operation cancelled");
                    return;
                }
            }
        };

        connection.Reconnecting += (error) =>
        {
            Console.WriteLine($"Reconnecting due to error: {error?.Message ?? "Unknown error"}");
            return Task.CompletedTask;
        };

        connection.Reconnected += (connectionId) =>
        {
            Console.WriteLine($"Reconnected with connection ID: {connectionId}");
            return Task.CompletedTask;
        };
    }

    public static async Task IndicateRenderError(HubConnection connection, string message)
    {
        await connection.SendAsync("RenderError", message);
    }
}