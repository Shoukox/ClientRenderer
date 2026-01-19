using Newtonsoft.Json;
using System.Collections.Concurrent;
namespace ClientRenderer.Render;

public class BeatmapsetsService
{
    private static HttpClient HttpClient { get; } = new() { Timeout = TimeSpan.FromSeconds(10) };

    private const string BaseUrlMino = "https://catboy.best/";
    private const string BaseUrlSyui = "https://syui.eternityglow.de/";
    private const string BaseUrlOsu = "https://osu.ppy.sh/beatmapsets/";

    public static ConcurrentDictionary<string, BeatmapsetInfo> HashToValues = new();

    public record BeatmapsetInfo
    {
        public int BeatmapsetId { get; set; }
        public int TotalLength { get; set; }
    }

    public async Task<Result<Stream>> DownloadBeatmapset(int beatmapsetId)
    {
        var downloadResult = await DownloadBeatmapViaSyui(beatmapsetId);

        // Downloading via osu is being done in Program.cs
        //if (!downloadResult.Success)
        //{
        //    downloadResult = await DownloadBeatmapViaOsu(beatmapsetId);
        //}

        if (!downloadResult.Success)
        {
            downloadResult = await DownloadBeatmapViaMino(beatmapsetId);
        }

        return downloadResult;
    }

    public async Task<Result<Stream>> DownloadBeatmapset(string beatmapMd5Hash)
    {
        var downloadResult = await DownloadBeatmapViaSyui(beatmapMd5Hash);

        if (!downloadResult.Success)
        {
            downloadResult = await DownloadBeatmapViaMino(beatmapMd5Hash);
        }

        return downloadResult;
    }

    /// <summary>
    /// NEEDS OSU_SESSION COOKIE
    /// </summary>
    /// <param name="beatmapsetId"></param>
    /// <returns></returns>
    public async Task<Result<Stream>> DownloadBeatmapViaOsu(int beatmapsetId, string osuSessionCookie)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrlOsu + $"{beatmapsetId}/download");
            SimulateBrowser(request);
            request.Headers.Add("Cookie", $"osu_session={osuSessionCookie}");
            request.Headers.Referrer = new Uri(BaseUrlOsu + $"{beatmapsetId}");
            var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            return Result<Stream>.FromSuccess(await response.Content.ReadAsStreamAsync());
        }
        catch (Exception e)
        {
            return Result<Stream>.FromFailure(e);
        }
    }

    private async Task<Result<Stream>> DownloadBeatmapViaSyui(int beatmapsetId)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrlSyui + $"d/{beatmapsetId}");
            SimulateBrowser(request);
            var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            return Result<Stream>.FromSuccess(await response.Content.ReadAsStreamAsync());
        }
        catch (Exception e)
        {
            return Result<Stream>.FromFailure(e);
        }
    }

    private async Task<Result<Stream>> DownloadBeatmapViaSyui(string beatmapMd5Hash)
    {
        Result<int> beatmapsetId = await GetBeatmapsetIdViaSyui(beatmapMd5Hash);
        if (!beatmapsetId.Success) return Result<Stream>.FromFailure(beatmapsetId.Exception!);

        HashToValues.AddOrUpdate(beatmapMd5Hash, 
            new BeatmapsetInfo() {  BeatmapsetId = beatmapsetId.Output }, 
            (k, b) =>
            {
                b.BeatmapsetId = beatmapsetId.Output;
                return b;
            });
        return await DownloadBeatmapViaSyui(beatmapsetId.Output);
    }

    private async Task<Result<Stream>> DownloadBeatmapViaMino(int beatmapsetId)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrlMino + $"d/{beatmapsetId}");
            SimulateBrowser(request);
            var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            return Result<Stream>.FromSuccess(await response.Content.ReadAsStreamAsync());
        }
        catch (Exception e)
        {
            return Result<Stream>.FromFailure(e);
        }
    }

    private async Task<Result<Stream>> DownloadBeatmapViaMino(string beatmapMd5Hash)
    {
        Result<int> beatmapsetId = await GetBeatmapsetIdViaMino(beatmapMd5Hash);
        if (!beatmapsetId.Success) return Result<Stream>.FromFailure(beatmapsetId.Exception!);

        HashToValues.AddOrUpdate(beatmapMd5Hash,
            new BeatmapsetInfo() { BeatmapsetId = beatmapsetId.Output },
            (k, b) =>
            {
                b.BeatmapsetId = beatmapsetId.Output;
                return b;
            });
        return await DownloadBeatmapViaMino(beatmapsetId.Output);
    }

    private async Task<Result<int>> GetBeatmapsetIdViaSyui(string beatmapMd5Hash)
    {
        try
        {
            string location = BaseUrlSyui + $"api/md5/{beatmapMd5Hash}";
            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, location);
            SimulateBrowser(httpRequest);
            var httpResponse = await HttpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead);
            httpResponse.EnsureSuccessStatusCode();
            var json = JsonConvert.DeserializeObject<dynamic>(await httpResponse.Content.ReadAsStringAsync());

            HashToValues.AddOrUpdate(beatmapMd5Hash,
                new BeatmapsetInfo() { TotalLength = (int)json!.TotalLength },
                (k, b) =>
                {
                    b.TotalLength = (int)json!.TotalLength;
                    return b;
                });
            return Result<int>.FromSuccess((int)json!.ParentSetID);
        }
        catch (Exception ex)
        {
            return Result<int>.FromFailure(ex);
        }
    }

    private async Task<Result<int>> GetBeatmapsetIdViaMino(string beatmapMd5Hash)
    {
        try
        {
            string location = BaseUrlMino + $"api/v2/md5/{beatmapMd5Hash}";
            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, location);
            SimulateBrowser(httpRequest);
            var httpResponse = await HttpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead);
            httpResponse.EnsureSuccessStatusCode();
            var json = JsonConvert.DeserializeObject<dynamic>(await httpResponse.Content.ReadAsStringAsync());

            HashToValues.AddOrUpdate(beatmapMd5Hash,
                new BeatmapsetInfo() { TotalLength = (int)json!.total_length },
                (k, b) =>
                {
                    b.TotalLength = (int)json!.total_length;
                    return b;
                });
            return Result<int>.FromSuccess((int)json!.beatmapset_id);
        }
        catch (Exception ex)
        {
            return Result<int>.FromFailure(ex);
        }
    }

    private void SimulateBrowser(HttpRequestMessage httpRequest)
    {
        httpRequest.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
                                               "AppleWebKit/537.36 (KHTML, like Gecko) " +
                                               "Chrome/115.0.0.0 Safari/537.36");
        httpRequest.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        httpRequest.Headers.Add("Referer", "https://catboy.best/");
    }

    public record Result<T>
    {
        public bool Success { get; init; }
        public T? Output { get; init; }
        public Exception? Exception { get; init; }

        public static Result<T> FromSuccess(T output) => new Result<T>
        {
            Success = true,
            Output = output,
            Exception = null
        };

        public static Result<T> FromFailure(Exception exception) => new Result<T>
        {
            Success = false,
            Output = default,
            Exception = exception
        };
    }
}