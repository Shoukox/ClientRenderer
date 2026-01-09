using Newtonsoft.Json;
using System.Net;
using static ClientRenderer.Render.BeatmapsetsService;

namespace ClientRenderer.Render;

public class BeatmapsetsService
{
    private static HttpClient HttpClient { get; } = new();

    private const string BaseUrlMino = "https://catboy.best/";
    private const string BaseUrlSyui = "https://syui.eternityglow.de/";
    private const string BaseUrlOsu = "https://osu.ppy.sh/beatmapsets/";

    public int LastBeatmapId;

    public async Task<Result<Stream>> DownloadBeatmapset(int beatmapsetId)
    {
        var downloadResult = await DownloadBeatmapViaSyui(beatmapsetId);

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
            var response = await HttpClient.SendAsync(request);
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
            var response = await HttpClient.SendAsync(request);
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
        Result<int> beatmapId = await GetBeatmapsetIdViaSyui(beatmapMd5Hash);
        if (!beatmapId.Success) return Result<Stream>.FromFailure(new HttpRequestException());
        LastBeatmapId = beatmapId.Output;
        return await DownloadBeatmapViaSyui(beatmapId.Output);
    }

    private async Task<Result<Stream>> DownloadBeatmapViaMino(int beatmapsetId)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrlMino + $"d/{beatmapsetId}");
            var response = await HttpClient.SendAsync(request);
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
        Result<int> beatmapId = await GetBeatmapsetIdViaMino(beatmapMd5Hash);
        if (!beatmapId.Success) return Result<Stream>.FromFailure(new HttpRequestException());
        LastBeatmapId = beatmapId.Output;
        return await DownloadBeatmapViaMino(beatmapId.Output);
    }

    private async Task<Result<int>> GetBeatmapsetIdViaSyui(string beatmapMd5Hash)
    {
        string location = BaseUrlSyui + $"api/md5/{beatmapMd5Hash}";
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, location);
        SimulateBrowser(httpRequest);
        var httpResponse = await HttpClient.SendAsync(httpRequest).ConfigureAwait(false);

        if (!httpResponse.IsSuccessStatusCode)
        {
            return Result<int>.FromFailure(new HttpRequestException(httpResponse.ReasonPhrase));
        }
        var json = JsonConvert.DeserializeObject<dynamic>(await httpResponse.Content.ReadAsStringAsync());
        return Result<int>.FromSuccess((int)json!.ParentSetID);
    }

    private async Task<Result<int>> GetBeatmapsetIdViaMino(string beatmapMd5Hash)
    {
        string location = BaseUrlMino + $"api/v2/md5/{beatmapMd5Hash}";
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, location);
        SimulateBrowser(httpRequest);
        var httpResponse = await HttpClient.SendAsync(httpRequest).ConfigureAwait(false);

        if (!httpResponse.IsSuccessStatusCode)
        {
            return Result<int>.FromFailure(new HttpRequestException(httpResponse.ReasonPhrase));
        }
        var json = JsonConvert.DeserializeObject<dynamic>(await httpResponse.Content.ReadAsStringAsync());
        return Result<int>.FromSuccess((int)json!.beatmapset_id);
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