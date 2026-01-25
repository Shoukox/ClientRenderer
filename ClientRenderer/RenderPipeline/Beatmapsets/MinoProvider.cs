using ClientRenderer.Helpers;
using ClientRenderer.Models;
using Newtonsoft.Json;

namespace ClientRenderer.RenderPipeline.Beatmapsets
{
    public class MinoProvider(HttpClient httpClient) : BeatmapsetsProviderBase
    {
        private const string BaseUrlMino = "https://catboy.best/";

        public override async Task<Result<Stream>> DownloadBeatmapset(string beatmapHash)
        {
            await SetBeatmapsetInfos(beatmapHash);

            int beatmapsetId = 0;
            if (HashToValues.TryGetValue(beatmapHash, out var result))
            {
                beatmapsetId = result.BeatmapsetId;
            }
            else
            {
                return Result<Stream>.FromFailure(new KeyNotFoundException(beatmapHash));
            }

            return await DownloadBeatmapset(beatmapsetId);
        }

        public override async Task<Result> SetBeatmapsetInfos(string beatmapHash)
        {
            try
            {
                string location = BaseUrlMino + $"api/v2/md5/{beatmapHash}";
                using var httpRequest = new HttpRequestMessage(HttpMethod.Get, location);
                BrowserHelper.SimulateBrowser(httpRequest);
                var httpResponse = await httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead);
                httpResponse.EnsureSuccessStatusCode();

                var json = JsonConvert.DeserializeObject<dynamic>(await httpResponse.Content.ReadAsStringAsync());

                int totalLength = (int)json!.total_length;
                HashToValues.AddOrUpdate(beatmapHash,
                    new BeatmapsetInfo() { TotalLength = totalLength },
                    (k, b) =>
                    {
                        b.TotalLength = totalLength;
                        return b;
                    });

                int beatmapsetId = (int)json!.beatmapset_id;
                HashToValues.AddOrUpdate(beatmapHash,
                    new BeatmapsetInfo() { BeatmapsetId = beatmapsetId },
                    (k, b) =>
                    {
                        b.BeatmapsetId = beatmapsetId;
                        return b;
                    });
                return Result.FromSuccess();
            }
            catch (Exception ex)
            {
                return Result.FromFailure(ex);
            }
        }

        private async Task<Result<Stream>> DownloadBeatmapset(int beatmapsetId)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrlMino + $"d/{beatmapsetId}");
                BrowserHelper.SimulateBrowser(request);
                var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
                return Result<Stream>.FromSuccess(await response.Content.ReadAsStreamAsync());
            }
            catch (Exception e)
            {
                return Result<Stream>.FromFailure(e);
            }
        }
    }
}
