using ClientRenderer.Models;

namespace ClientRenderer.Abstractions;

public interface IBeatmapsetsDownloader
{
    Task<bool> DownloadBeatmapset(RenderPipelineInfo info, IServerConnection serverConnection);
}
