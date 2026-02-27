using ClientRenderer.Models;

namespace ClientRenderer.Abstractions;

public interface IReplaysDownloader
{
    Task<bool> DownloadReplay(RenderPipelineInfo info, IServerConnection serverConnection);
}
