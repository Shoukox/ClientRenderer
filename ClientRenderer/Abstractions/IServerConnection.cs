using ClientRenderer.Models;

namespace ClientRenderer.Abstractions;

public interface IServerConnection : IAsyncDisposable
{
    Task<bool> InitializeToken();
    Task<RenderJob?> GetNextRenderJob(int intervalMs = 2000);
    Task<RenderJob?> GetRenderJobInfo(int jobId);
    Task<byte[]> DownloadReplay(int jobId);
    Task<byte[]> DownloadSkin(string skinFileNameHex);
    Task ReportRenderingProgress(int jobId, double progress);
    Task FinishRendering(int jobId);
    Task SetRenderJobMetadata(RenderJob renderJob);
    Task Failure(int jobId, string reason, bool rerender = true);
    Task PostVideo(string videoPath, int jobId, int chunkSizeBytes = 5 * 1024 * 1024);
    Task UploadThumbnail(string thumbnailPath, int jobId);
}
