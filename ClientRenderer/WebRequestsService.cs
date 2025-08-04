using System.Net.Http.Headers;

namespace ClientRenderer;

public static class WebRequestsService
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromMinutes(30) };
    public static string ServerUrl = null!; // will be set in Program class

    public static async Task PostVideoAsync(string videoPath, string jobMessage)
    {
        await using var fileStream = new FileStream(videoPath, FileMode.Open, FileAccess.Read);

        using var formContent = new MultipartFormDataContent();
        using var streamContent = new StreamContent(fileStream);

        streamContent.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
        formContent.Add(streamContent, "file", Path.GetFileName(videoPath));

        var response = await HttpClient.PostAsync(
            Flurl.Url.Combine(ServerUrl, "upload-video") + $"?message={jobMessage}",
            formContent);

        response.EnsureSuccessStatusCode();
    }

    public static async Task<byte[]> DownloadReplayAsync(string replayName)
    {
        var response = await HttpClient.GetAsync(
            Flurl.Url.Combine(ServerUrl, "get-replay")+$"?replayName={replayName}");
        
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync();
    }
}