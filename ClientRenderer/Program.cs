using ClientRenderer;
using ClientRenderer.Render;
using ClientRenderer.Utils;
using DanserWrapper;
using Flurl;
using Microsoft.AspNetCore.SignalR.Client;

if (args.Length < 2 || args[0] != "-s" || !Uri.IsWellFormedUriString(args[1], UriKind.Absolute))
{
    Console.WriteLine("Usage: ./ClientRenderer.exe -s <url> -encoder <h264_nvenc/libx264>");
    return;
}

string chosenEncoder = "h264_nvenc"; // default nvenc
if (args.Length == 4 && args[3] is "libx264")
{
    chosenEncoder = args[3];
}

DanserGo.AdjustDanserGoPath(Environment.OSVersion);
if (!DanserGo.DanserExists())
{
    Console.WriteLine("Danser-go does not exist!");
    return;
}
DanserGo.AdjustConfig(chosenEncoder);
Console.WriteLine($"{chosenEncoder} has been set as a default danser encoder.");
DanserGo.CreateDirectoriesIfNeeded();

ConsoleService.ConfigureConsoleClose(out var token);

string hubName = "render-job-hub";
string hubUrl = Url.Combine(args[1], hubName);
WebRequestsService.ServerUrl = args[1];

ReplaysService.LoadAllBeatmapsHashes();

while (!token.IsCancellationRequested)
{
    try
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect()
            .Build();
        await connection.StartAsync();
        HubService.SetupEventHandlers(connection, token);
        Console.WriteLine("Connected to SignalR hub successfully!");
        Console.WriteLine($"Connection ID: {connection.ConnectionId}");

        await Task.Delay(Timeout.Infinite, token);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error connecting to SignalR hub: {ex.Message}");
        Console.WriteLine($"Retrying in 5 seconds...");
    }

    try
    {
        await Task.Delay(5000, token);
    }
    catch (OperationCanceledException ex)
    {
        Console.WriteLine(ex.Message);
        return;
    }
}