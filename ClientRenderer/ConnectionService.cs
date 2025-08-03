using DanserWrapper;
using Microsoft.AspNetCore.SignalR.Client;

namespace ClientRenderer;

public static class ConnectionService
{
    public static void SetupEventHandlers(HubConnection connection)
    {
        // Listen for messages from the server
        // Replace "ReceiveMessage" with your actual hub method name
        connection.On<string, Task>("RenderJob", async (jobMessage) =>
        {
            Console.WriteLine($"Got a new job! Replay name: {jobMessage}");
            Console.WriteLine($"Downloading replay...");

            string tempReplayPath;
            try
            {
                tempReplayPath = Path.GetTempFileName();
                await File.WriteAllBytesAsync(tempReplayPath, await WebRequestsService.DownloadReplayAsync(jobMessage));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to download replay: {ex.Message}");
                await IndicateRenderError(connection, jobMessage);
                return;
            }


            Console.WriteLine($"Downloaded!");
            Console.WriteLine($"Start rendering");

            string videoFileName = Path.GetFileNameWithoutExtension(jobMessage);
            var result = await new DanserGo()
                .ExecuteAsync($"-r \"{tempReplayPath}\" " +
                              $"-out \"{videoFileName}\"");

            if (!result.Success)
            {
                Console.WriteLine($"Failed to render replay!");
                await IndicateRenderError(connection, jobMessage);
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
                return;
            }

            Console.WriteLine($"Successfully uploaded");
        });

        connection.On<string>("ReceiveMessage",
            (notification) => { Console.WriteLine($"[NOTIFICATION] {notification}"); });

        connection.Closed += async (error) =>
        {
            Console.WriteLine($"Connection closed: {error?.Message ?? "No error"}");
            await Task.Delay(5000);

            try
            {
                await connection.StartAsync();
                Console.WriteLine("Reconnected!");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Reconnection failed: {ex.Message}");
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