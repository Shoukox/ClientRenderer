using ClientRenderer.Helpers;
using ClientRenderer.Models;
using OsuApi.BanchoV2;

namespace ClientRenderer.RenderPipeline.Beatmapsets
{
    public class OsuProvider(BanchoApiV2 osuApi, string osuSessionCookie, HttpClient httpClient) : BeatmapsetsProviderBase
    {
        private const string BaseUrlOsu = "https://osu.ppy.sh/";

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
            var lookupBeatmapResponse = await osuApi.Beatmaps.LookupBeatmap(new() { Checksum = beatmapHash });
            if (lookupBeatmapResponse?.BeatmapExtended is null)
            {
                return Result.FromFailure(new NullReferenceException("lookupBeatmapResponse?.BeatmapExtended is null"));
            }

            HashToValues.AddOrUpdate(beatmapHash,
                    new BeatmapsetInfo() { BeatmapsetId = lookupBeatmapResponse.BeatmapExtended.BeatmapsetId!.Value },
                    (k, b) =>
                    {
                        b.BeatmapsetId = lookupBeatmapResponse.BeatmapExtended.BeatmapsetId!.Value;
                        return b;
                    });
            HashToValues.AddOrUpdate(beatmapHash,
                    new BeatmapsetInfo() { TotalLength = lookupBeatmapResponse.BeatmapExtended.TotalLength ?? 0 },
                    (k, b) =>
                    {
                        b.TotalLength = lookupBeatmapResponse.BeatmapExtended.TotalLength ?? 0;
                        return b;
                    });

            return Result.FromSuccess();
        }

        private async Task<Result<Stream>> DownloadBeatmapset(int beatmapsetId)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrlOsu + $"beatmapsets/{beatmapsetId}/download");
                BrowserHelper.SimulateBrowser(request);
                request.Headers.Add("Cookie", $"osu_session={osuSessionCookie}");
                request.Headers.Referrer = new Uri(BaseUrlOsu + $"{beatmapsetId}");
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
