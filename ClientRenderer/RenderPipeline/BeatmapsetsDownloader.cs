using ClientRenderer.Connection;
using ClientRenderer.Logging;
using ClientRenderer.Models;
using ClientRenderer.RenderPipeline.Beatmapsets;
using DanserWrapper;
using OsuApi.BanchoV2;
using System.IO.Compression;
namespace ClientRenderer.Render;

public class BeatmapsetsDownloader(BanchoApiV2 banchoApiV2, string osuSessionCookie)
{
    private static HttpClient s_HttpClient { get; } = new() { Timeout = TimeSpan.FromSeconds(10) };

    private List<string> BeatmapsMd5Hashes = new();

    private readonly object _locker = new();

    public BeatmapsetsProviderBase[] BeatmapsetsProviders = [
                new SyuiProvider(s_HttpClient),
                new MinoProvider(s_HttpClient),
                new OsuProvider(banchoApiV2, osuSessionCookie, s_HttpClient),
    ];

    public async Task<string> CreateMd5(string path)
    {
        byte[] inputBytes = await File.ReadAllBytesAsync(path);
        return await CreateMd5(inputBytes);
    }

    public async Task<string> CreateMd5(byte[] bytes)
    {
        byte[] inputBytes = bytes;

        using System.Security.Cryptography.MD5 md5 = System.Security.Cryptography.MD5.Create();
        byte[] hashBytes = md5.ComputeHash(inputBytes);
        return Convert.ToHexString(hashBytes);
    }

    public void LoadAllBeatmapsHashes()
    {
        lock (_locker)
        {
            BeatmapsMd5Hashes = new();
            foreach (string dir in Directory.GetDirectories(DanserGo.SongsPath))
            {
                var beatmaps = Directory.GetFiles(dir).Where(m => m.EndsWith(".osu"));
                foreach (string beatmap in beatmaps)
                {
                    BeatmapsMd5Hashes.Add(CreateMd5(beatmap).GetAwaiter().GetResult().ToLowerInvariant());
                }
            }
        }
    }

    public bool BeatmapExists(string beatmapHash)
    {
        bool exists;
        lock (_locker)
        {
            exists = BeatmapsMd5Hashes.Any(m => m.Equals(beatmapHash, StringComparison.InvariantCultureIgnoreCase));
        }

        return exists;
    }

    public async Task<Result<Stream>> DownloadBeatmapsetAsStream(string beatmapHash)
    {
        foreach (var provider in BeatmapsetsProviders)
        {
            var downloadResult = await provider.DownloadBeatmapset(beatmapHash);
            if (downloadResult.Success)
            {
                return downloadResult;
            }
        }

        return Result<Stream>.FromFailure(new Exception("Failed to download a beatmapset"));
    }

    public async Task<Result> SetBeatmapsetInfos(string beatmapHash)
    {
        foreach (var provider in BeatmapsetsProviders.Reverse())
        {
            var result = await provider.SetBeatmapsetInfos(beatmapHash);
            if (result.Success)
            {
                return result;
            }
        }

        return Result.FromFailure(new Exception("Failed to set beatmapset infos"));
    }

    public async Task<bool> DownloadBeatmapset(RenderPipelineInfo info, ServerConnection serverConnection, string osuSessionCookie)
    {
        await serverConnection.ReportRenderingProgress(info.RenderJob!.JobId, -1);
        Logger.Log($"[JobId:{info.RenderJob!.JobId}] Downloading a beatmap...");

        string oszFileName = $"{info.BeatmapHash}.osz";
        info.BeatmapsetOszPath = Path.Combine(AppContext.BaseDirectory, oszFileName);
        string beatmapsetDirectoryPath = Path.Combine(DanserGo.SongsPath, oszFileName);
        LoadAllBeatmapsHashes();
        if (!BeatmapExists(info.BeatmapHash))
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

            using (var oszStream = downloadResult.Output!)
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
                ZipFile.ExtractToDirectory(oszStreamCopy, beatmapsetDirectoryPath);
            }
            Logger.Log($"[JobId:{info.RenderJob!.JobId}] Sucessfully downloaded beatmapset! (.osz)");
        }
        else
        {
            if (info.UseExperimentalRenderer)
            {
                if (File.Exists(info.BeatmapsetOszPath))
                {
                    File.Delete(info.BeatmapsetOszPath);
                }
                ZipFile.CreateFromDirectory(beatmapsetDirectoryPath, info.BeatmapsetOszPath);
            }
            Logger.Log($"[JobId:{info.RenderJob!.JobId}] Beatmap exists locally, proceeding to render...");
        }

        if (!BeatmapsetsProviderBase.HashToValues.TryGetValue(info.BeatmapHash, out var beatmapsetInfo) || beatmapsetInfo.TotalLength is null)
        {
            var result = await SetBeatmapsetInfos(info.BeatmapHash);
            if (!result.Success)
            {
                await serverConnection.Failure(info.RenderJob.JobId, "Failed to retrieve beatmapset infos", false);
                Logger.LogError($"[JobId:{info.RenderJob!.JobId}] Failed to retrieve beatmapset infos...");
                return false;
            }
        }
        info.BeatmapLength = BeatmapsetsProviderBase.HashToValues[info.BeatmapHash].TotalLength;

        return true;
    }
}