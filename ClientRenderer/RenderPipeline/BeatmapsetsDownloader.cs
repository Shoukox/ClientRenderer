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

    private async Task<bool> BeatmapsetDirectoryContainsHash(string beatmapsetDirectoryPath, string beatmapHash)
    {
        foreach (string beatmap in Directory.EnumerateFiles(beatmapsetDirectoryPath, "*.osu", SearchOption.AllDirectories))
        {
            string md5 = await CreateMd5(beatmap);
            if (md5.Equals(beatmapHash, StringComparison.InvariantCultureIgnoreCase))
                return true;
        }

        return false;
    }

    public async Task<Result> SaveBeatmapsetStreamAsFile(RenderPipelineInfo info, Stream beatmapsetStream)
    {
        using (var oszStream = beatmapsetStream)
        {
            var oszStreamCopy = new MemoryStream();

            await oszStream.CopyToAsync(oszStreamCopy);
            oszStreamCopy.Position = 0;

            if (oszStreamCopy.Length == 0)
                return Result.FromFailure(new InvalidDataException("Downloaded beatmapset is empty."));

            string tempSuffix = $".download-{Guid.NewGuid():N}";
            string tempBeatmapsetDirectoryPath = info.BeatmapsetDirectoryPath + tempSuffix;
            string tempBeatmapsetOszPath = info.BeatmapsetOszPath + tempSuffix;

            try
            {
                if (info.UseExperimentalRenderer)
                {
                    using var fs = new FileStream(tempBeatmapsetOszPath, FileMode.CreateNew, FileAccess.Write);
                    await oszStreamCopy.CopyToAsync(fs);
                    oszStreamCopy.Position = 0;
                }

                ZipFile.ExtractToDirectory(oszStreamCopy, tempBeatmapsetDirectoryPath);

                if (!await BeatmapsetDirectoryContainsHash(tempBeatmapsetDirectoryPath, info.BeatmapHash))
                    return Result.FromFailure(new InvalidDataException("Downloaded beatmapset archive does not contain the requested beatmap."));

                if (Directory.Exists(info.BeatmapsetDirectoryPath))
                    Directory.Delete(info.BeatmapsetDirectoryPath, true);

                Directory.Move(tempBeatmapsetDirectoryPath, info.BeatmapsetDirectoryPath);

                if (info.UseExperimentalRenderer)
                {
                    if (File.Exists(info.BeatmapsetOszPath))
                        File.Delete(info.BeatmapsetOszPath);

                    File.Move(tempBeatmapsetOszPath, info.BeatmapsetOszPath);
                }

                return Result.FromSuccess();
            }
            catch (InvalidDataException ex)
            {
                return Result.FromFailure(new InvalidDataException("Downloaded beatmapset archive is corrupt or is not a valid .osz/.zip file.", ex));
            }
            catch (Exception ex)
            {
                return Result.FromFailure(ex);
            }
            finally
            {
                if (Directory.Exists(tempBeatmapsetDirectoryPath))
                    Directory.Delete(tempBeatmapsetDirectoryPath, true);

                if (File.Exists(tempBeatmapsetOszPath))
                    File.Delete(tempBeatmapsetOszPath);
            }
        }
    }

    private async Task<Result> DownloadAndSaveValidBeatmapset(RenderPipelineInfo info)
    {
        Exception? lastException = null;

        foreach (var provider in BeatmapsetsProviders)
        {
            string providerName = provider.GetType().Name;
            Logger.Log($"[JobId:{info.RenderJob!.JobId}] Downloading beatmapset using {providerName}...");

            var downloadResult = await provider.DownloadBeatmapset(info.BeatmapHash);
            if (!downloadResult.Success)
            {
                lastException = downloadResult.Exception;
                Logger.LogError($"[JobId:{info.RenderJob!.JobId}] {providerName} failed to download beatmapset: {downloadResult.Exception!.Message}");
                continue;
            }

            var saveResult = await SaveBeatmapsetStreamAsFile(info, downloadResult.Output!);
            if (saveResult.Success)
                return Result.FromSuccess();

            lastException = saveResult.Exception;
            Logger.LogError($"[JobId:{info.RenderJob!.JobId}] {providerName} returned an invalid beatmapset, trying another provider. Error: {saveResult.Exception!.Message}");
        }

        return Result.FromFailure(lastException ?? new Exception("Failed to download a valid beatmapset."));
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

            var downloadResult = await DownloadAndSaveValidBeatmapset(info);
            if (!downloadResult.Success)
            {
                await serverConnection.Failure(info.RenderJob.JobId, "beatmapset_download_failed", false);
                Logger.LogError($"[JobId:{info.RenderJob!.JobId}] Failed to download a valid beatmapset!");
                Logger.LogError($"Error. Your osu_session cookie is probably expired. Renew it. Error message: {downloadResult.Exception!.Message}");
                return false;
            }

            LoadAllBeatmapsHashes(force: true);
            if (!BeatmapExists(info.BeatmapHash))
            {
                await serverConnection.Failure(info.RenderJob.JobId, "beatmapset_download_failed", false);
                Logger.LogError($"[JobId:{info.RenderJob!.JobId}] Downloaded beatmapset does not contain the requested beatmap!");
                return false;
            }

            Logger.Log($"[JobId:{info.RenderJob!.JobId}] Successfully downloaded beatmapset! (.osz)");
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
