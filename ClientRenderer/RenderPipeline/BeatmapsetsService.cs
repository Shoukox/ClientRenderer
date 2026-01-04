using Newtonsoft.Json;
using System.Net;

namespace ClientRenderer.Render;

public class BeatmapsetsService
{
    private static HttpClient HttpClient { get; } = new();

    private const string BaseUrlMino = "https://catboy.best/";
    private const string BaseUrlSyui = "https://syui.eternityglow.de/";
    private const string BaseUrlOsu = "https://osu.ppy.sh/beatmapsets/";

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

    /// <summary>
    /// NEEDS OSU_SESSION COOKIE
    /// </summary>
    /// <param name="beatmapsetId"></param>
    /// <returns></returns>
    private async Task<Result<Stream>> DownloadBeatmapViaOsu(int beatmapsetId)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrlOsu + $"{beatmapsetId}/download");
            SimulateBrowser(request);

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

    public async Task<int?> GetBeatmapsetId(string beatmapMd5Hash, Source source = Source.Syui)
    {
        if (source == Source.Osu)
        {
            source = Source.Syui;
        }
        string location = source switch
        {
            Source.Mino => BaseUrlMino + $"api/v2/md5/{beatmapMd5Hash}",
            Source.Syui => BaseUrlSyui + $"api/md5/{beatmapMd5Hash}",
            _ => throw new NotImplementedException()
        };
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, location);
        SimulateBrowser(httpRequest);

        // Send request
        var httpResponse = await HttpClient.SendAsync(httpRequest).ConfigureAwait(false);
        if (!httpResponse.IsSuccessStatusCode)
        {
            return null;
        }

        string json = await httpResponse.Content.ReadAsStringAsync();
        var jsonObject = JsonConvert.DeserializeObject<dynamic>(json);
        int beatmapsetId = -1;
        if (source == Source.Mino)
        {
            beatmapsetId = jsonObject!.beatmapset_id;
        }
        else if (source == Source.Syui)
        {
            beatmapsetId = jsonObject!.ParentSetID;
        }
        else throw new NotImplementedException();
        return beatmapsetId;
    }

    private void SimulateBrowser(HttpRequestMessage httpRequest)
    {
        httpRequest.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
                                               "AppleWebKit/537.36 (KHTML, like Gecko) " +
                                               "Chrome/115.0.0.0 Safari/537.36");
        httpRequest.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        httpRequest.Headers.Add("Referer", "https://catboy.best/");
    }

    public enum Source
    {
        Osu = 0,
        Mino = 1,
        Syui = 2
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