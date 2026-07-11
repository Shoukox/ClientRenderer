using ClientRenderer.Abstractions;
using ClientRenderer.Logging;
using ClientRenderer.Models;
using ClientRenderer.Startup;
using OsuParsers.Decoders;
using OsuParsers.Replays;

namespace ClientRenderer.Render;

public class ReplaysDownloader : IReplaysDownloader
{
    public Replay DecodeReplay(byte[] replayBytes)
    {
        return ReplayDecoder.Decode(new MemoryStream(replayBytes));
    }

    public async Task<bool> DownloadReplay(RenderPipelineInfo info, IServerConnection serverConnection)
    {
        try
        {
            await serverConnection.ReportRenderingProgress(info.RenderJob!.JobId, -2);
            Logger.Log($"[JobId:{info.RenderJob!.JobId}] Downloading replay...");
            info.ReplayAsBytes = await serverConnection.DownloadReplay(info.RenderJob!.JobId);
            info.DecodedReplay = DecodeReplay(info.ReplayAsBytes);
            info.RenderJob.PlayerName = info.DecodedReplay.PlayerName;
            if (info.DecodedReplay.Ruleset != OsuParsers.Enums.Ruleset.Standard)
            {
                info.UseExperimentalRenderer = true;
                Logger.Log($"[JobId:{info.RenderJob!.JobId}] Replay uses {info.DecodedReplay.Ruleset}; switching to experimental renderer.");
            }

            info.BeatmapHash = info.DecodedReplay.BeatmapMD5Hash;
            info.ReplayPath = Path.Combine(AppStoragePaths.GetDownloadsDirectory("replays"), $"{info.BeatmapHash}{info.FileTimeNow}.osr");
            await File.WriteAllBytesAsync(info.ReplayPath, info.ReplayAsBytes);
            Logger.Log($"[JobId:{info.RenderJob!.JobId}] Replay saved to: {info.ReplayPath}");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, $"[JobId:{info.RenderJob!.JobId}] Failed to download or decode replay.");
            throw;
        }

        return true;
    }
}
