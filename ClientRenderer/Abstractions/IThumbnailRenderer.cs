using ClientRenderer.Models;

namespace ClientRenderer.Abstractions;

public interface IThumbnailRenderer
{
    Task<bool> RenderThumbnail(RenderPipelineInfo info, IServerConnection serverConnection, CancellationToken cancellationToken, int timeoutMs = 10_000);
}
