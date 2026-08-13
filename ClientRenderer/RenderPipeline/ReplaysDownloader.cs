using ClientRenderer.Abstractions;
using ClientRenderer.Logging;
using ClientRenderer.Models;
using ClientRenderer.Startup;
using OsuApi.BanchoV2;
using OsuApi.BanchoV2.Clients.Beatmaps.HttpIO;
using OsuApi.BanchoV2.Users.Models;
using OsuParsers.Decoders;
using OsuParsers.Replays;

namespace ClientRenderer.Render;

public class ReplaysDownloader : IReplaysDownloader
{
    private readonly BanchoApiV2 _osuApi;

    public ReplaysDownloader(BanchoApiV2 osuApi)
    {
        _osuApi = osuApi;
    }

    public Replay DecodeReplay(byte[] replayBytes)
    {
        return ReplayDecoder.Decode(new MemoryStream(replayBytes));
    }

    public async Task<bool> DownloadReplay(RenderPipelineInfo info, IServerConnection serverConnection)
    {
        if (info.RenderJob!.RenderSettings.UseAutoPlay)
        {
            return await PrepareAutoplay(info, serverConnection);
        }

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

    private async Task<bool> PrepareAutoplay(RenderPipelineInfo info, IServerConnection serverConnection)
    {
        try
        {
            await serverConnection.ReportRenderingProgress(info.RenderJob!.JobId, -2);

            int beatmapId = info.RenderJob.RenderSettings.AutoBeatmapId
                ?? throw new InvalidOperationException("Autoplay render does not contain a beatmap id.");

            Logger.Log($"[JobId:{info.RenderJob.JobId}] Resolving autoplay beatmap {beatmapId}...");
            GetBeatmapResponse? response = await _osuApi.Beatmaps.GetBeatmap(beatmapId);
            BeatmapExtended? beatmap = response?.BeatmapExtended;
            if (beatmap?.Id is null || string.IsNullOrWhiteSpace(beatmap.Checksum))
            {
                throw new InvalidOperationException("beatmap_not_found");
            }

            info.BeatmapId = beatmap.Id.Value;
            info.BeatmapHash = beatmap.Checksum;
            info.BeatmapLength = beatmap.TotalLength;
            info.ReplayPath = string.Empty;
            info.ReplayAsBytes = [];
            info.RenderJob.PlayerName = "Auto-play";
            Logger.Log($"[JobId:{info.RenderJob.JobId}] Autoplay beatmap resolved: {info.BeatmapId} ({info.BeatmapHash}).");
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, $"[JobId:{info.RenderJob!.JobId}] Failed to prepare autoplay beatmap.");
            throw;
        }
    }
}
