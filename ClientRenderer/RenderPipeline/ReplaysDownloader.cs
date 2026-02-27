using ClientRenderer.Abstractions;
using ClientRenderer.Logging;
using ClientRenderer.Models;
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
        await serverConnection.ReportRenderingProgress(info.RenderJob!.JobId, -2);
        Logger.Log($"[JobId:{info.RenderJob!.JobId}] Downloading a replay...");
        info.ReplayAsBytes = await serverConnection.DownloadReplay(info.RenderJob!.JobId);
        info.DecodedReplay = DecodeReplay(info.ReplayAsBytes);
        info.RenderJob.PlayerName = info.DecodedReplay.PlayerName;
        if (info.DecodedReplay.Ruleset != OsuParsers.Enums.Ruleset.Standard)
        {
            info.UseExperimentalRenderer = true;
        }
        info.BeatmapHash = info.DecodedReplay.BeatmapMD5Hash;
        info.ReplayPath = Path.GetFullPath($"{info.BeatmapHash}{info.FileTimeNow}.osr");
        await File.WriteAllBytesAsync(info.ReplayPath, info.ReplayAsBytes);

        return true;
    }
}