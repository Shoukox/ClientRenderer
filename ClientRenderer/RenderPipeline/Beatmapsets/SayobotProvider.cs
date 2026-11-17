using ClientRenderer.Helpers;
using ClientRenderer.Logging;
using ClientRenderer.Models;
using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;

namespace ClientRenderer.RenderPipeline.Beatmapsets
{
    public class SayobotProvider(HttpClient httpClient, ConcurrentDictionary<string, BeatmapsetInfo> hashToValues) : BeatmapsetsProviderBase(hashToValues)
    {
        private const string BaseUrlSayobotApi = "https://api.sayobot.cn/";
        private const string BaseUrlSayobotDownload = "https://dl.sayobot.cn/";

        public override async Task<Result<BeatmapsetDownload>> DownloadBeatmapset(string beatmapHash, bool includeVideo)
        {
            await SetBeatmapsetInfos(beatmapHash);

            if (!HashToValues.TryGetValue(beatmapHash, out var result))
            {
                Logger.LogWarning($"SayobotProvider could not find beatmap info for hash: {beatmapHash}");
                return Result<BeatmapsetDownload>.FromFailure(new KeyNotFoundException(beatmapHash));
            }

            return await DownloadBeatmapset(result.BeatmapsetId, includeVideo);
        }

        public override async Task<Result> SetBeatmapsetInfos(string beatmapHash)
        {
            if (!HashToValues.TryGetValue(beatmapHash, out var result))
                return Result.FromFailure(new KeyNotFoundException(beatmapHash));

            try
            {
                string location = BaseUrlSayobotApi + $"v2/beatmapinfo?0={result.BeatmapsetId}";
                using HttpRequestMessage httpRequest = new HttpRequestMessage(HttpMethod.Get, location);
                BrowserHelper.SimulateBrowser(httpRequest);
                var httpResponse = await httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead);
                httpResponse.EnsureSuccessStatusCode();

                var json = JObject.Parse(await httpResponse.Content.ReadAsStringAsync());
                int? totalLength = json["data"]?["bid_data"]?.OfType<JObject>()
                    .Select(beatmap => (int?)beatmap["length"])
                    .Where(length => length is > 0)
                    .Max();

                if (totalLength is not null)
                {
                    HashToValues.AddOrUpdate(beatmapHash,
                        new BeatmapsetInfo { TotalLength = totalLength, BeatmapsetId = result.BeatmapsetId },
                        (_, b) => { b.TotalLength ??= totalLength; return b; });
                }

                Logger.Log($"SayobotProvider found beatmapset {result.BeatmapsetId} for hash: {beatmapHash}");
                return Result.FromSuccess();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, $"SayobotProvider failed to look up beatmap hash: {beatmapHash}");
                return Result.FromFailure(ex);
            }
        }

        private async Task<Result<BeatmapsetDownload>> DownloadBeatmapset(int beatmapsetId, bool includeVideo)
        {
            try
            {
                using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, BaseUrlSayobotDownload + $"beatmaps/download/{(includeVideo ? "full" : "novideo")}/{beatmapsetId}");
                BrowserHelper.SimulateBrowser(request);
                BeatmapsetDownload download = await OpenDownloadAsync(httpClient, request);
                Logger.Log($"SayobotProvider started downloading beatmapset {beatmapsetId} ({FormatSize(download.ContentLength)}).");
                return Result<BeatmapsetDownload>.FromSuccess(download);
            }
            catch (Exception e)
            {
                Logger.LogError(e, $"SayobotProvider failed to download beatmapset {beatmapsetId}.");
                return Result<BeatmapsetDownload>.FromFailure(e);
            }
        }
    }
}
