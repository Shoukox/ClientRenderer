using ClientRenderer.Models;
using System.Collections.Concurrent;

namespace ClientRenderer.RenderPipeline.Beatmapsets
{
    public abstract class BeatmapsetsProviderBase(ConcurrentDictionary<string, BeatmapsetInfo> hashToValues)
    {
        protected ConcurrentDictionary<string, BeatmapsetInfo> HashToValues { get; } = hashToValues;

        public abstract Task<Result<Stream>> DownloadBeatmapset(string beatmapHash);
        public abstract Task<Result> SetBeatmapsetInfos(string beatmapHash);
    }
}
