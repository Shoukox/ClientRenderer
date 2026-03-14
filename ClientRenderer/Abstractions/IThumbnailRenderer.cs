using ClientRenderer.Models;

namespace ClientRenderer.Abstractions;

public interface IThumbnailRenderer
{
    Task<bool> RenderThumbnail(RenderPipelineInfo info, IServerConnection serverConnection);
}
