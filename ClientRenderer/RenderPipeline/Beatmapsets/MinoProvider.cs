using ClientRenderer.Helpers;
using ClientRenderer.Logging;
using ClientRenderer.Models;
using Newtonsoft.Json;
using System.Collections.Concurrent;

namespace ClientRenderer.RenderPipeline.Beatmapsets
{
    public class MinoProvider(HttpClient httpClient, ConcurrentDictionary<string, BeatmapsetInfo> hashToValues) : BeatmapsetsProviderBase(hashToValues)
    {
        private const string BaseUrlMino = "https://catboy.best/";

        public override async Task<Result<Stream>> DownloadBeatmapset(string beatmapHash)
        {
            await SetBeatmapsetInfos(beatmapHash);

            if (!HashToValues.TryGetValue(beatmapHash, out var result))
            {
                Logger.LogWarning($"MinoProvider could not find beatmap info for hash: {beatmapHash}");
                return Result<Stream>.FromFailure(new KeyNotFoundException(beatmapHash));
            }

            return await DownloadBeatmapset(result.BeatmapsetId);
        }

        public override async Task<Result> SetBeatmapsetInfos(string beatmapHash)
        {
            try
            {
                string location = BaseUrlMino + $"api/v2/md5/{beatmapHash}";
                using HttpRequestMessage httpRequest = new HttpRequestMessage(HttpMethod.Get, location);
                BrowserHelper.SimulateBrowser(httpRequest);
                var httpResponse = await httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead);
                httpResponse.EnsureSuccessStatusCode();

                var json = JsonConvert.DeserializeObject<dynamic>(await httpResponse.Content.ReadAsStringAsync());

                int totalLength = (int)json!.total_length;
                int beatmapsetId = (int)json!.beatmapset_id;

                HashToValues.AddOrUpdate(beatmapHash,
                    new BeatmapsetInfo { TotalLength = totalLength, BeatmapsetId = beatmapsetId },
                    (_, b) => { b.TotalLength = totalLength; b.BeatmapsetId = beatmapsetId; return b; });

                Logger.Log($"MinoProvider found beatmapset {beatmapsetId} for hash: {beatmapHash}");
                return Result.FromSuccess();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, $"MinoProvider failed to look up beatmap hash: {beatmapHash}");
                return Result.FromFailure(ex);
            }
        }

        private async Task<Result<Stream>> DownloadBeatmapset(int beatmapsetId)
        {
            try
            {
                using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, BaseUrlMino + $"d/{beatmapsetId}");
                BrowserHelper.SimulateBrowser(request);
                var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
                Logger.Log($"MinoProvider downloaded beatmapset {beatmapsetId}.");
                return Result<Stream>.FromSuccess(await response.Content.ReadAsStreamAsync());
            }
            catch (Exception e)
            {
                Logger.LogError(e, $"MinoProvider failed to download beatmapset {beatmapsetId}.");
                return Result<Stream>.FromFailure(e);
            }
        }
    }
}
