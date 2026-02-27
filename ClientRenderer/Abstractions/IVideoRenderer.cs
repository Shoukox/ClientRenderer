using ClientRenderer.Models;

namespace ClientRenderer.Abstractions;

public interface IVideoRenderer
{
    Task<bool> RenderVideo(RenderPipelineInfo info, IServerConnection serverConnection, CancellationToken cancellationToken);
}
