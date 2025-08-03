using System.Net;
using Newtonsoft.Json;

namespace ClientRenderer;

public static class OsuBeatmapsetsService
{
    private static HttpClient HttpClient { get; } = new();
    private const string BaseUrl = "https://catboy.best/";
    
    public static async Task<Stream> DownloadBeatmapset(int beatmapsetId)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, BaseUrl + $"d/{beatmapsetId}");
        SimulateBrowser(httpRequest);
        
        // Send request
        var httpResponse = await HttpClient.SendAsync(httpRequest).ConfigureAwait(false);
        if (!httpResponse.IsSuccessStatusCode)
        {
            if (httpResponse.StatusCode == HttpStatusCode.NotFound) throw new HttpRequestException(HttpRequestError.HttpProtocolError);

            throw new HttpRequestException(
                $"Request failed with status code {(int)httpResponse.StatusCode} ({httpResponse.StatusCode}).");
        }

        return await httpResponse.Content.ReadAsStreamAsync();
    }

    public static async Task<int> GetBeatmapsetId(string beatmapMd5Hash)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, BaseUrl + $"api/v2/md5/{beatmapMd5Hash}");
        SimulateBrowser(httpRequest);
            
        // Send request
        var httpResponse = await HttpClient.SendAsync(httpRequest).ConfigureAwait(false);
        if (!httpResponse.IsSuccessStatusCode)
        {
            if (httpResponse.StatusCode == HttpStatusCode.NotFound) throw new HttpRequestException(HttpRequestError.HttpProtocolError);

            throw new HttpRequestException(
                $"Request failed with status code {(int)httpResponse.StatusCode} ({httpResponse.StatusCode}).");
        }
        
        string json = await httpResponse.Content.ReadAsStringAsync();
        int beatmapsetId = JsonConvert.DeserializeObject<dynamic>(json)!.beatmapset_id;

        return beatmapsetId;
    }

    private static void SimulateBrowser(HttpRequestMessage httpRequest)
    {
        httpRequest.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
                                               "AppleWebKit/537.36 (KHTML, like Gecko) " +
                                               "Chrome/115.0.0.0 Safari/537.36");
        httpRequest.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        httpRequest.Headers.Add("Referer", "https://catboy.best/");
    }
}