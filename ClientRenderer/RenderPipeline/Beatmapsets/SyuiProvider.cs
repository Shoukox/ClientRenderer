using ClientRenderer.Helpers;
using ClientRenderer.Logging;
using ClientRenderer.Models;
using Newtonsoft.Json;
using System.Collections.Concurrent;

namespace ClientRenderer.RenderPipeline.Beatmapsets
{
    public class SyuiProvider(HttpClient httpClient, ConcurrentDictionary<string, BeatmapsetInfo> hashToValues) : BeatmapsetsProviderBase(hashToValues)
    {
        private const string BaseUrlSyui = "https://syui.eternityglow.de/";

        public override async Task<Result<Stream>> DownloadBeatmapset(string beatmapHash)
        {
            await SetBeatmapsetInfos(beatmapHash);

            if (!HashToValues.TryGetValue(beatmapHash, out var result))
            {
                Logger.LogWarning($"SyuiProvider could not find beatmap info for hash: {beatmapHash}");
                return Result<Stream>.FromFailure(new KeyNotFoundException(beatmapHash));
            }

            return await DownloadBeatmapset(result.BeatmapsetId);
        }

        public override async Task<Result> SetBeatmapsetInfos(string beatmapHash)
        {
            try
            {
                string location = BaseUrlSyui + $"api/md5/{beatmapHash}";
                using HttpRequestMessage httpRequest = new HttpRequestMessage(HttpMethod.Get, location);
                BrowserHelper.SimulateBrowser(httpRequest);
                var httpResponse = await httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead);
                httpResponse.EnsureSuccessStatusCode();
                var json = JsonConvert.DeserializeObject<dynamic>(await httpResponse.Content.ReadAsStringAsync());

                int totalLength = (int)json!.TotalLength;
                int beatmapsetId = (int)json!.ParentSetID;

                HashToValues.AddOrUpdate(beatmapHash,
                    new BeatmapsetInfo { TotalLength = totalLength, BeatmapsetId = beatmapsetId },
                    (_, b) => { b.TotalLength = totalLength; b.BeatmapsetId = beatmapsetId; return b; });

                Logger.Log($"SyuiProvider found beatmapset {beatmapsetId} for hash: {beatmapHash}");
                return Result.FromSuccess();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, $"SyuiProvider failed to look up beatmap hash: {beatmapHash}");
                return Result.FromFailure(ex);
            }
        }

        private async Task<Result<Stream>> DownloadBeatmapset(int beatmapsetId)
        {
            try
            {
                using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, BaseUrlSyui + $"d/{beatmapsetId}");
                BrowserHelper.SimulateBrowser(request);
                var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
                Logger.Log($"SyuiProvider downloaded beatmapset {beatmapsetId}.");
                return Result<Stream>.FromSuccess(await response.Content.ReadAsStreamAsync());
            }
            catch (Exception e)
            {
                Logger.LogError(e, $"SyuiProvider failed to download beatmapset {beatmapsetId}.");
                return Result<Stream>.FromFailure(e);
            }
        }
    }
}
