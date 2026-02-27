using ClientRenderer.Models;

namespace ClientRenderer.Abstractions;

public interface ISkinsDownloader
{
    Task<bool> DownloadSkin(RenderPipelineInfo info, IServerConnection serverConnection);
}
