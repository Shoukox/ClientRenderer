using ClientRenderer.Models;
using System.Collections.Concurrent;

namespace ClientRenderer.RenderPipeline.Beatmapsets
{
    public abstract class BeatmapsetsProviderBase
    {
        public static ConcurrentDictionary<string, BeatmapsetInfo> HashToValues = new();

        public abstract Task<Result<Stream>> DownloadBeatmapset(string beatmapHash);
        public abstract Task<Result> SetBeatmapsetInfos(string beatmapHash);
    }
}
