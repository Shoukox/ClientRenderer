using ClientRenderer.Models;
using System.Collections.Concurrent;

namespace ClientRenderer.RenderPipeline.Beatmapsets
{
    public abstract class BeatmapsetsProviderBase(ConcurrentDictionary<string, BeatmapsetInfo> hashToValues)
    {
        protected ConcurrentDictionary<string, BeatmapsetInfo> HashToValues { get; } = hashToValues;

        /// <summary>
        /// Starts a beatmapset download. Only the response headers have been received when this
        /// returns; <see cref="ClientRenderer.Render.BeatmapsetsDownloader"/> reads the body and
        /// checks its speed.
        /// </summary>
        /// <param name="includeVideo">
        /// When false, providers that support it return the archive without the background video,
        /// which is often most of its size.
        /// </param>
        public abstract Task<Result<BeatmapsetDownload>> DownloadBeatmapset(string beatmapHash, bool includeVideo);
        public abstract Task<Result> SetBeatmapsetInfos(string beatmapHash);

        protected static async Task<BeatmapsetDownload> OpenDownloadAsync(HttpClient httpClient, HttpRequestMessage request)
        {
            HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            try
            {
                response.EnsureSuccessStatusCode();
                Stream content = await response.Content.ReadAsStreamAsync();
                return new BeatmapsetDownload(content, response.Content.Headers.ContentLength);
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }

        protected static string FormatSize(long? bytes)
            => bytes is { } value ? $"{value / 1024.0 / 1024.0:0.0} MiB" : "unknown size";
    }
}
