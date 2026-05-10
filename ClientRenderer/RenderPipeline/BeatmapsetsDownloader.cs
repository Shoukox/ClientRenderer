using ClientRenderer.Abstractions;
using ClientRenderer.Logging;
using ClientRenderer.Models;
using ClientRenderer.RenderPipeline.Beatmapsets;
using DanserWrapper;
using OsuApi.BanchoV2;
using System.Collections.Concurrent;
using System.IO.Compression;

namespace ClientRenderer.Render;

public class BeatmapsetsDownloader : IBeatmapsetsDownloader
{
    private static HttpClient s_HttpClient { get; } = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly ConcurrentDictionary<string, BeatmapsetInfo> _hashToValues = new();
    private List<string> BeatmapsMd5Hashes = new();
    private DateTime _lastHashesReloadAtUtc = DateTime.MinValue;
    private static readonly TimeSpan HashesReloadInterval = TimeSpan.FromMinutes(5);

    private readonly object _locker = new();

    public BeatmapsetsProviderBase[] BeatmapsetsProviders { get; }
    public BeatmapsetsProviderBase FallbackProvider => BeatmapsetsProviders.Last();

    public BeatmapsetsDownloader(BanchoApiV2 banchoApiV2, string osuSessionCookie)
    {
        BeatmapsetsProviders =
        [
            new SyuiProvider(s_HttpClient, _hashToValues),
            new MinoProvider(s_HttpClient, _hashToValues),
            new OsuProvider(banchoApiV2, osuSessionCookie, s_HttpClient, _hashToValues),
        ];
    }

    public async Task<string> CreateMd5(string path)
    {
        byte[] inputBytes = await File.ReadAllBytesAsync(path);
        return await CreateMd5(inputBytes);
    }

    public Task<string> CreateMd5(byte[] bytes)
    {
        using System.Security.Cryptography.MD5 md5 = System.Security.Cryptography.MD5.Create();
        byte[] hashBytes = md5.ComputeHash(bytes);
        return Task.FromResult(Convert.ToHexString(hashBytes));
    }

    public void LoadAllBeatmapsHashes(bool force = false)
    {
        lock (_locker)
        {
            if (!force && BeatmapsMd5Hashes.Count > 0 && DateTime.UtcNow - _lastHashesReloadAtUtc < HashesReloadInterval)
                return;

            BeatmapsMd5Hashes = new();
            foreach (string dir in Directory.GetDirectories(DanserGo.SongsPath))
            {
                var beatmaps = Directory.GetFiles(dir).Where(m => m.EndsWith(".osu"));
                foreach (string beatmap in beatmaps)
                    BeatmapsMd5Hashes.Add(CreateMd5(beatmap).GetAwaiter().GetResult().ToLowerInvariant());
            }

            _lastHashesReloadAtUtc = DateTime.UtcNow;
        }
    }

    public bool BeatmapExists(string beatmapHash)
    {
        lock (_locker)
            return BeatmapsMd5Hashes.Any(m => m.Equals(beatmapHash, StringComparison.InvariantCultureIgnoreCase));
    }

    public async Task<Result<Stream>> DownloadBeatmapsetAsStream(string beatmapHash)
    {
        foreach (var provider in BeatmapsetsProviders)
        {
            var downloadResult = await provider.DownloadBeatmapset(beatmapHash);
            if (downloadResult.Success)
                return downloadResult;
        }

        return Result<Stream>.FromFailure(new Exception("Failed to download a beatmapset"));
    }

    public async Task<Result<Stream>> DownloadBeatmapsetAsStreamUsingFallbackProvider(string beatmapHash)
    {
        var downloadResult = await FallbackProvider.DownloadBeatmapset(beatmapHash);
        return downloadResult.Success
            ? downloadResult
            : Result<Stream>.FromFailure(new Exception("Failed to download a beatmapset"));
    }

    public async Task<Result> SetBeatmapsetInfos(string beatmapHash)
    {
        foreach (var provider in BeatmapsetsProviders.Reverse())
        {
            var result = await provider.SetBeatmapsetInfos(beatmapHash);
            if (result.Success)
                return result;
        }

        return Result.FromFailure(new Exception("Failed to set beatmapset infos"));
    }

    public async Task SaveBeatmapsetStreamAsFile(RenderPipelineInfo info, Stream beatmapsetStream)
    {
        using (var oszStream = beatmapsetStream)
        {
            var oszStreamCopy = new MemoryStream();

            await oszStream.CopyToAsync(oszStreamCopy);
            oszStreamCopy.Position = 0;

            if (info.UseExperimentalRenderer)
            {
                using var fs = new FileStream(info.BeatmapsetOszPath, FileMode.Create, FileAccess.Write);
                await oszStreamCopy.CopyToAsync(fs);
                oszStreamCopy.Position = 0;
            }

            if (Directory.Exists(info.BeatmapsetDirectoryPath))
                Directory.Delete(info.BeatmapsetDirectoryPath, true);

            ZipFile.ExtractToDirectory(oszStreamCopy, info.BeatmapsetDirectoryPath);
        }
    }

    public async Task<bool> DownloadBeatmapset(RenderPipelineInfo info, IServerConnection serverConnection)
    {
        await serverConnection.ReportRenderingProgress(info.RenderJob!.JobId, -1);
        Logger.Log($"[JobId:{info.RenderJob!.JobId}] Downloading a beatmap...");

        string oszFileName = $"{info.BeatmapHash}.osz";
        info.BeatmapsetOszPath = Path.Combine(AppContext.BaseDirectory, oszFileName);
        info.BeatmapsetDirectoryPath = Path.Combine(DanserGo.SongsPath, oszFileName);
        LoadAllBeatmapsHashes();

        if (!BeatmapExists(info.BeatmapHash) || !DanserGo.BeatmapDirectoryExists(info.BeatmapsetDirectoryPath))
        {
            Logger.Log($"[JobId:{info.RenderJob!.JobId}] The requested beatmap does not exist!");
            Logger.Log($"[JobId:{info.RenderJob!.JobId}] Downloading beatmapset...");

            var downloadResult = await DownloadBeatmapsetAsStream(info.BeatmapHash);
            if (!downloadResult.Success)
            {
                await serverConnection.Failure(info.RenderJob.JobId, "beatmapset_download_failed", false);
                Logger.LogError($"[JobId:{info.RenderJob!.JobId}] Failed to download a beatmapset!");
                Logger.LogError($"Error. Your osu_session cookie is probably expired. Renew it. Error message: {downloadResult.Exception!.Message}");
                return false;
            }

            await SaveBeatmapsetStreamAsFile(info, downloadResult.Output!);
            LoadAllBeatmapsHashes(force: true);
            if (!BeatmapExists(info.BeatmapHash))
            {
                downloadResult = await DownloadBeatmapsetAsStreamUsingFallbackProvider(info.BeatmapHash);
                if (!downloadResult.Success)
                {
                    await serverConnection.Failure(info.RenderJob.JobId, "beatmapset_download_failed", false);
                    Logger.LogError($"[JobId:{info.RenderJob!.JobId}] Failed to download a beatmapset!");
                    Logger.LogError($"Error. Your osu_session cookie is probably expired. Renew it. Error message: {downloadResult.Exception!.Message}");
                    return false;
                }

                await SaveBeatmapsetStreamAsFile(info, downloadResult.Output!);
                LoadAllBeatmapsHashes(force: true);
            }

            Logger.Log($"[JobId:{info.RenderJob!.JobId}] Sucessfully downloaded beatmapset! (.osz)");
        }
        else
        {
            if (info.UseExperimentalRenderer)
            {
                if (File.Exists(info.BeatmapsetOszPath))
                    File.Delete(info.BeatmapsetOszPath);

                ZipFile.CreateFromDirectory(info.BeatmapsetDirectoryPath, info.BeatmapsetOszPath);
            }
            Logger.Log($"[JobId:{info.RenderJob!.JobId}] Beatmap exists locally, proceeding to render...");
        }

        if (!_hashToValues.TryGetValue(info.BeatmapHash, out var beatmapsetInfo) || beatmapsetInfo.TotalLength is null)
        {
            var result = await SetBeatmapsetInfos(info.BeatmapHash);
            if (!result.Success)
            {
                await serverConnection.Failure(info.RenderJob.JobId, "Failed to retrieve beatmapset infos", false);
                Logger.LogError($"[JobId:{info.RenderJob!.JobId}] Failed to retrieve beatmapset infos...");
                return false;
            }
        }

        info.BeatmapLength = _hashToValues[info.BeatmapHash].TotalLength;
        return true;
    }
}
