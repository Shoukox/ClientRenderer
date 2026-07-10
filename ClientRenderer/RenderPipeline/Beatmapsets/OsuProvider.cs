using ClientRenderer.Helpers;
using ClientRenderer.Logging;
using ClientRenderer.Models;
using OsuApi.BanchoV2;
using OsuApi.BanchoV2.Clients.Beatmaps.HttpIO;
using System.Collections.Concurrent;

namespace ClientRenderer.RenderPipeline.Beatmapsets
{
    public class OsuProvider(BanchoApiV2 osuApi, string osuSessionCookie, HttpClient httpClient, ConcurrentDictionary<string, BeatmapsetInfo> hashToValues)
        : BeatmapsetsProviderBase(hashToValues)
    {
        private const string BaseUrlOsu = "https://osu.ppy.sh/";

        public override async Task<Result<Stream>> DownloadBeatmapset(string beatmapHash)
        {
            await SetBeatmapsetInfos(beatmapHash);

            if (!HashToValues.TryGetValue(beatmapHash, out var result))
            {
                Logger.LogWarning($"OsuProvider could not find beatmap info for hash: {beatmapHash}");
                return Result<Stream>.FromFailure(new KeyNotFoundException(beatmapHash));
            }

            return await DownloadBeatmapset(result.BeatmapsetId);
        }

        public override async Task<Result> SetBeatmapsetInfos(string beatmapHash)
        {
            LookupBeatmapResponse lookupBeatmapResponse;
            try
            {
                lookupBeatmapResponse = await osuApi.Beatmaps.LookupBeatmap(new() { Checksum = beatmapHash });
            }
            catch (Exception e)
            {
                Logger.LogError(e, $"OsuProvider failed to look up beatmap hash: {beatmapHash}");
                return Result.FromFailure(new NullReferenceException("The requested beatmap was not found", e));
            }
            if (lookupBeatmapResponse?.BeatmapExtended is null)
            {
                Logger.LogWarning($"OsuProvider lookup response did not contain beatmap details for hash: {beatmapHash}");
                return Result.FromFailure(new NullReferenceException("lookupBeatmapResponse?.BeatmapExtended is null"));
            }

            int beatmapsetId = lookupBeatmapResponse.BeatmapExtended.BeatmapsetId!.Value;
            int totalLength = lookupBeatmapResponse.BeatmapExtended.TotalLength ?? 0;

            HashToValues.AddOrUpdate(beatmapHash,
                new BeatmapsetInfo { BeatmapsetId = beatmapsetId, TotalLength = totalLength },
                (_, b) => { b.BeatmapsetId = beatmapsetId; b.TotalLength = totalLength; return b; });

            return Result.FromSuccess();
        }

        private async Task<Result<Stream>> DownloadBeatmapset(int beatmapsetId)
        {
            try
            {
                using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, BaseUrlOsu + $"beatmapsets/{beatmapsetId}/download");
                BrowserHelper.SimulateBrowser(request);
                request.Headers.Add("Cookie", $"osu_session={osuSessionCookie}");
                request.Headers.Referrer = new Uri(BaseUrlOsu + $"{beatmapsetId}");
                var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
                Logger.Log($"OsuProvider downloaded beatmapset {beatmapsetId}.");
                return Result<Stream>.FromSuccess(await response.Content.ReadAsStreamAsync());
            }
            catch (Exception e)
            {
                Logger.LogError(e, $"OsuProvider failed to download beatmapset {beatmapsetId}.");
                return Result<Stream>.FromFailure(e);
            }
        }
    }
}
