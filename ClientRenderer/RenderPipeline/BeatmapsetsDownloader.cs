using ClientRenderer.Abstractions;
using ClientRenderer.Logging;
using ClientRenderer.Models;
using ClientRenderer.RenderPipeline.Beatmapsets;
using ClientRenderer.Startup;
using DanserWrapper;
using OsuApi.BanchoV2;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;

namespace ClientRenderer.Render;

public class BeatmapsetsDownloader : IBeatmapsetsDownloader
{
    private const string BeatmapNotFoundFailureReason = "beatmap_not_found";
    private const string BeatmapsetDownloadFailedFailureReason = "beatmapset_download_failed";

    // HttpClient.Timeout only covers the response headers (providers use ResponseHeadersRead),
    // so the body transfer is policed here instead.
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SpeedCheckGracePeriod = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SpeedWindow = TimeSpan.FromSeconds(5);
    // Judged on the remaining time, so an almost finished download is never thrown away.
    private static readonly TimeSpan MaxProjectedRemainingDuration = TimeSpan.FromSeconds(10);
    private const double MinBytesPerSecondWhenSizeUnknown = 512 * 1024;
    private static readonly TimeSpan SlowProviderPenalty = TimeSpan.FromMinutes(30);
    // The server closes a job as "Stuck" after 10 minutes without a progress report.
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(60);

    private static HttpClient s_HttpClient { get; } = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly ConcurrentDictionary<string, BeatmapsetInfo> _hashToValues = new();
    private List<string> BeatmapsMd5Hashes = new();
    private DateTime _lastHashesReloadAtUtc = DateTime.MinValue;
    private static readonly TimeSpan HashesReloadInterval = TimeSpan.FromMinutes(5);
    private readonly Dictionary<BeatmapsetsProviderBase, DateTime> _slowProvidersUntilUtc = new();

    private readonly object _locker = new();

    /// <summary>
    /// Providers in their default priority. Syui must stay ahead of Sayobot: Sayobot cannot look up
    /// a beatmap hash itself and reuses the beatmapset id found by an earlier provider.
    /// </summary>
    public BeatmapsetsProviderBase[] BeatmapsetsProviders { get; }

    public BeatmapsetsDownloader(BanchoApiV2 banchoApiV2, string osuSessionCookie)
    {
        BeatmapsetsProviders =
        [
            new SyuiProvider(s_HttpClient, _hashToValues),
            new SayobotProvider(s_HttpClient, _hashToValues),
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
            Logger.Log($"Loaded {BeatmapsMd5Hashes.Count} local beatmap hashes.");
        }
    }

    public bool BeatmapExists(string beatmapHash)
    {
        lock (_locker)
            return BeatmapsMd5Hashes.Any(m => m.Equals(beatmapHash, StringComparison.InvariantCultureIgnoreCase));
    }

    public async Task<Result> SetBeatmapsetInfos(string beatmapHash)
    {
        foreach (var provider in BeatmapsetsProviders.Reverse())
        {
            var result = await provider.SetBeatmapsetInfos(beatmapHash);
            if (result.Success)
                return result;
        }

        return Result.FromFailure(new Exception("Failed to set beatmapset info"));
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

    private async Task<Result> SaveBeatmapsetArchive(RenderPipelineInfo info, MemoryStream oszStream)
    {
        string tempSuffix = $".download-{Guid.NewGuid():N}";
        string tempBeatmapsetDirectoryPath = info.BeatmapsetDirectoryPath + tempSuffix;
        string tempBeatmapsetOszPath = info.BeatmapsetOszPath + tempSuffix;

        try
        {
            if (info.UseExperimentalRenderer)
            {
                using FileStream fs = new FileStream(tempBeatmapsetOszPath, FileMode.CreateNew, FileAccess.Write);
                await oszStream.CopyToAsync(fs);
                oszStream.Position = 0;
                Logger.Log($"[JobId:{info.RenderJob!.JobId}] Beatmapset archive saved for experimental renderer: {tempBeatmapsetOszPath}");
            }

            ZipFile.ExtractToDirectory(oszStream, tempBeatmapsetDirectoryPath);
            Logger.Log($"[JobId:{info.RenderJob!.JobId}] Beatmapset archive extracted to: {tempBeatmapsetDirectoryPath}");

            if (!await BeatmapsetDirectoryContainsHash(tempBeatmapsetDirectoryPath, info.BeatmapHash))
            {
                Logger.LogError($"[JobId:{info.RenderJob!.JobId}] Downloaded beatmapset archive does not contain beatmap hash {info.BeatmapHash}.");
                return Result.FromFailure(new InvalidDataException("The downloaded beatmapset archive does not contain the requested beatmap."));
            }

            if (Directory.Exists(info.BeatmapsetDirectoryPath))
                Directory.Delete(info.BeatmapsetDirectoryPath, true);

            Directory.Move(tempBeatmapsetDirectoryPath, info.BeatmapsetDirectoryPath);
            Logger.Log($"[JobId:{info.RenderJob!.JobId}] Beatmapset moved to: {info.BeatmapsetDirectoryPath}");

            if (info.UseExperimentalRenderer)
            {
                if (File.Exists(info.BeatmapsetOszPath))
                    File.Delete(info.BeatmapsetOszPath);

                File.Move(tempBeatmapsetOszPath, info.BeatmapsetOszPath);
                Logger.Log($"[JobId:{info.RenderJob!.JobId}] Beatmapset archive moved to: {info.BeatmapsetOszPath}");
            }

            return Result.FromSuccess();
        }
        catch (InvalidDataException ex)
        {
            Logger.LogError(ex, $"[JobId:{info.RenderJob!.JobId}] Downloaded beatmapset archive is invalid.");
            return Result.FromFailure(new InvalidDataException("The downloaded beatmapset archive is corrupt or is not a valid .osz/.zip file.", ex));
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, $"[JobId:{info.RenderJob!.JobId}] Failed to save downloaded beatmapset.");
            return Result.FromFailure(ex);
        }
        finally
        {
            if (Directory.Exists(tempBeatmapsetDirectoryPath))
            {
                Directory.Delete(tempBeatmapsetDirectoryPath, true);
                Logger.Log($"[JobId:{info.RenderJob!.JobId}] Deleted temporary beatmapset directory: {tempBeatmapsetDirectoryPath}");
            }

            if (File.Exists(tempBeatmapsetOszPath))
            {
                File.Delete(tempBeatmapsetOszPath);
                Logger.Log($"[JobId:{info.RenderJob!.JobId}] Deleted temporary beatmapset archive: {tempBeatmapsetOszPath}");
            }
        }
    }

    private async Task<Result> DownloadAndSaveValidBeatmapset(RenderPipelineInfo info, IServerConnection serverConnection)
    {
        Exception? lastException = null;
        List<BeatmapsetsProviderBase> providers = GetProvidersInPreferredOrder();
        List<(BeatmapsetsProviderBase Provider, double BytesPerSecond)> slowButWorkingProviders = new();

        for (int i = 0; i < providers.Count; i++)
        {
            // The last provider has no alternative left, so it is allowed to be slow.
            var attempt = await TryProvider(providers[i], enforceMinimumSpeed: i < providers.Count - 1);
            if (attempt.Success)
                return attempt;

            lastException = attempt.Exception;
            if (attempt.Exception is BeatmapsetDownloadTooSlowException { Stalled: false } tooSlow)
                slowButWorkingProviders.Add((providers[i], tooSlow.BytesPerSecond));
        }

        // Everything else failed: a slow download is still better than a failed job.
        foreach (var provider in slowButWorkingProviders.OrderByDescending(m => m.BytesPerSecond).Select(m => m.Provider))
        {
            Logger.LogWarning($"[JobId:{info.RenderJob!.JobId}] All providers failed. Retrying {provider.GetType().Name} without the speed limit.");
            var attempt = await TryProvider(provider, enforceMinimumSpeed: false);
            if (attempt.Success)
                return attempt;

            lastException = attempt.Exception;
        }

        return Result.FromFailure(lastException ?? new Exception("Failed to download a valid beatmapset."));

        async Task<Result> TryProvider(BeatmapsetsProviderBase provider, bool enforceMinimumSpeed)
        {
            string providerName = provider.GetType().Name;
            Logger.Log($"[JobId:{info.RenderJob!.JobId}] Downloading beatmapset using {providerName}...");

            var downloadResult = await provider.DownloadBeatmapset(info.BeatmapHash, includeVideo: info.RenderJob.RenderSettings.Video);
            if (!downloadResult.Success)
            {
                Logger.LogError(downloadResult.Exception!, $"[JobId:{info.RenderJob!.JobId}] {providerName} failed to download beatmapset.");
                return Result.FromFailure(downloadResult.Exception!);
            }

            var readResult = await ReadBeatmapsetAsync(downloadResult.Output!, providerName, enforceMinimumSpeed, info, serverConnection);
            if (!readResult.Success)
            {
                if (readResult.Exception is BeatmapsetDownloadTooSlowException)
                    MarkProviderSlow(provider);

                Logger.LogError(readResult.Exception!, $"[JobId:{info.RenderJob!.JobId}] {providerName} failed to transfer the beatmapset. Trying another provider.");
                return Result.FromFailure(readResult.Exception!);
            }

            using MemoryStream oszStream = readResult.Output!;
            var saveResult = await SaveBeatmapsetArchive(info, oszStream);
            if (!saveResult.Success)
                Logger.LogError(saveResult.Exception!, $"[JobId:{info.RenderJob!.JobId}] {providerName} returned an invalid beatmapset. Trying another provider.");

            return saveResult;
        }
    }

    private async Task<Result<MemoryStream>> ReadBeatmapsetAsync(
        BeatmapsetDownload download,
        string providerName,
        bool enforceMinimumSpeed,
        RenderPipelineInfo info,
        IServerConnection serverConnection)
    {
        await using var _ = download;
        int initialCapacity = download.ContentLength is > 0 and <= int.MaxValue ? (int)download.ContentLength.Value : 0;
        MemoryStream buffer = new MemoryStream(initialCapacity);
        byte[] chunk = new byte[81920];
        Stopwatch stopwatch = Stopwatch.StartNew();
        TimeSpan lastKeepAlive = TimeSpan.Zero;
        // (elapsed, bytes received) samples over the last SpeedWindow. Mirrors often send the first
        // megabytes fast and then throttle, so the average speed would hide the slowdown.
        Queue<(TimeSpan Elapsed, long Bytes)> speedSamples = new();
        speedSamples.Enqueue((TimeSpan.Zero, 0));
        using CancellationTokenSource stallCts = new CancellationTokenSource();

        try
        {
            while (true)
            {
                int read;
                stallCts.CancelAfter(StallTimeout);
                try
                {
                    read = await download.Content.ReadAsync(chunk, stallCts.Token);
                }
                catch (OperationCanceledException ex) when (stallCts.IsCancellationRequested)
                {
                    throw new BeatmapsetDownloadTooSlowException(
                        $"{providerName} sent no data for {StallTimeout.TotalSeconds:0} seconds.", stalled: true, ex);
                }

                if (read == 0)
                    break;

                buffer.Write(chunk, 0, read);
                TimeSpan elapsed = stopwatch.Elapsed;
                speedSamples.Enqueue((elapsed, buffer.Length));
                while (speedSamples.Count > 2 && elapsed - speedSamples.ElementAt(1).Elapsed >= SpeedWindow)
                    speedSamples.Dequeue();

                var windowStart = speedSamples.Peek();
                double recentBytesPerSecond = (buffer.Length - windowStart.Bytes) /
                                              Math.Max((elapsed - windowStart.Elapsed).TotalSeconds, 0.001);

                if (enforceMinimumSpeed && elapsed >= SpeedCheckGracePeriod &&
                    GetSlownessReason(buffer.Length, download.ContentLength, recentBytesPerSecond) is { } slownessReason)
                {
                    throw new BeatmapsetDownloadTooSlowException($"{providerName} is too slow: {slownessReason}", stalled: false)
                    {
                        BytesPerSecond = buffer.Length / elapsed.TotalSeconds
                    };
                }

                if (elapsed - lastKeepAlive >= KeepAliveInterval)
                {
                    lastKeepAlive = elapsed;
                    await KeepJobAliveAsync(info, serverConnection);
                }
            }

            if (buffer.Length == 0)
                throw new InvalidDataException($"{providerName} returned an empty beatmapset.");

            if (download.ContentLength is { } expectedLength && buffer.Length != expectedLength)
                throw new InvalidDataException($"{providerName} returned {buffer.Length} of {expectedLength} bytes.");

            double seconds = Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
            Logger.Log($"[JobId:{info.RenderJob!.JobId}] {providerName} transferred {buffer.Length / 1024.0 / 1024.0:0.0} MiB " +
                       $"in {seconds:0.0} s ({buffer.Length / seconds / 1024.0 / 1024.0:0.00} MiB/s).");

            buffer.Position = 0;
            return Result<MemoryStream>.FromSuccess(buffer);
        }
        catch (Exception ex)
        {
            await buffer.DisposeAsync();
            return Result<MemoryStream>.FromFailure(ex);
        }
    }

    private static string? GetSlownessReason(long receivedBytes, long? contentLength, double bytesPerSecond)
    {
        if (contentLength is > 0)
        {
            TimeSpan remaining = TimeSpan.FromSeconds(Math.Max(0, contentLength.Value - receivedBytes) / bytesPerSecond);
            return remaining > MaxProjectedRemainingDuration
                ? $"{bytesPerSecond / 1024.0:0} KiB/s, the rest of the archive would take about {remaining.TotalSeconds:0} s"
                : null;
        }

        return bytesPerSecond < MinBytesPerSecondWhenSizeUnknown
            ? $"{bytesPerSecond / 1024.0:0} KiB/s"
            : null;
    }

    private static async Task KeepJobAliveAsync(RenderPipelineInfo info, IServerConnection serverConnection)
    {
        try
        {
            await serverConnection.ReportRenderingProgress(info.RenderJob!.JobId, -1);
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"[JobId:{info.RenderJob!.JobId}] Failed to report beatmapset download activity: {ex.Message}");
        }
    }

    private List<BeatmapsetsProviderBase> GetProvidersInPreferredOrder()
    {
        lock (_locker)
        {
            DateTime now = DateTime.UtcNow;
            foreach (var expired in _slowProvidersUntilUtc.Where(m => m.Value <= now).Select(m => m.Key).ToList())
                _slowProvidersUntilUtc.Remove(expired);

            return BeatmapsetsProviders.Where(p => !_slowProvidersUntilUtc.ContainsKey(p))
                .Concat(BeatmapsetsProviders.Where(p => _slowProvidersUntilUtc.ContainsKey(p)))
                .ToList();
        }
    }

    private void MarkProviderSlow(BeatmapsetsProviderBase provider)
    {
        lock (_locker)
            _slowProvidersUntilUtc[provider] = DateTime.UtcNow + SlowProviderPenalty;

        Logger.LogWarning($"{provider.GetType().Name} will be tried last for the next {SlowProviderPenalty.TotalMinutes:0} minutes.");
    }

    private sealed class BeatmapsetDownloadTooSlowException(string message, bool stalled, Exception? innerException = null)
        : TimeoutException(message, innerException)
    {
        /// <summary>True when no data arrived at all; false when data arrived, just too slowly.</summary>
        public bool Stalled { get; } = stalled;

        /// <summary>Average speed measured before the download was aborted.</summary>
        public double BytesPerSecond { get; init; }
    }

    public async Task<bool> DownloadBeatmapset(RenderPipelineInfo info, IServerConnection serverConnection)
    {
        await serverConnection.ReportRenderingProgress(info.RenderJob!.JobId, -1);
        Logger.Log($"[JobId:{info.RenderJob!.JobId}] Preparing beatmap files...");

        string oszFileName = $"{info.BeatmapHash}.osz";
        info.BeatmapsetOszPath = Path.Combine(AppStoragePaths.GetDownloadsDirectory("beatmaps"), oszFileName);
        info.BeatmapsetDirectoryPath = Path.Combine(DanserGo.SongsPath, oszFileName);
        LoadAllBeatmapsHashes();

        if (!BeatmapExists(info.BeatmapHash) || !DanserGo.BeatmapDirectoryExists(info.BeatmapsetDirectoryPath))
        {
            Logger.Log($"[JobId:{info.RenderJob!.JobId}] Requested beatmap was not found locally.");
            Logger.Log($"[JobId:{info.RenderJob!.JobId}] Downloading beatmapset...");

            var downloadResult = await DownloadAndSaveValidBeatmapset(info, serverConnection);
            if (!downloadResult.Success)
            {
                string failureReason = downloadResult.Exception is KeyNotFoundException
                    ? BeatmapNotFoundFailureReason
                    : BeatmapsetDownloadFailedFailureReason;

                await serverConnection.Failure(info.RenderJob.JobId, failureReason, false);
                Logger.LogError(downloadResult.Exception!, $"[JobId:{info.RenderJob!.JobId}] Failed to download a valid beatmapset. Failure reason: {failureReason}.");
                return false;
            }

            LoadAllBeatmapsHashes(force: true);
            if (!BeatmapExists(info.BeatmapHash))
            {
                await serverConnection.Failure(info.RenderJob.JobId, BeatmapsetDownloadFailedFailureReason, false);
                Logger.LogError($"[JobId:{info.RenderJob!.JobId}] Downloaded beatmapset does not contain the requested beatmap.");
                return false;
            }

            Logger.Log($"[JobId:{info.RenderJob!.JobId}] Beatmapset downloaded successfully.");
        }
        else
        {
            if (info.UseExperimentalRenderer)
            {
                if (File.Exists(info.BeatmapsetOszPath))
                    File.Delete(info.BeatmapsetOszPath);

                ZipFile.CreateFromDirectory(info.BeatmapsetDirectoryPath, info.BeatmapsetOszPath);
                Logger.Log($"[JobId:{info.RenderJob!.JobId}] Beatmapset archive created for experimental renderer: {info.BeatmapsetOszPath}");
            }
            Logger.Log($"[JobId:{info.RenderJob!.JobId}] Beatmap exists locally. Proceeding to render.");
        }

        if (!_hashToValues.TryGetValue(info.BeatmapHash, out var beatmapsetInfo) || beatmapsetInfo.TotalLength is null)
        {
            var result = await SetBeatmapsetInfos(info.BeatmapHash);
            if (!result.Success)
            {
                await serverConnection.Failure(info.RenderJob.JobId, "Failed to retrieve beatmapset info", false);
                Logger.LogError(result.Exception!, $"[JobId:{info.RenderJob!.JobId}] Failed to retrieve beatmapset info.");
                return false;
            }
        }

        info.BeatmapLength = _hashToValues[info.BeatmapHash].TotalLength;
        Logger.Log($"[JobId:{info.RenderJob!.JobId}] Beatmap length: {info.BeatmapLength} seconds.");
        return true;
    }
}
