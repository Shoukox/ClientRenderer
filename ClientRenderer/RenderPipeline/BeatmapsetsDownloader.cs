using ClientRenderer.Abstractions;
using ClientRenderer.Logging;
using ClientRenderer.Models;
using ClientRenderer.RenderPipeline.Beatmapsets;
using ClientRenderer.Startup;
using DanserWrapper;
using OsuApi.BanchoV2;
using System.Collections.Concurrent;
using System.IO.Compression;

namespace ClientRenderer.Render;

public class BeatmapsetsDownloader : IBeatmapsetsDownloader
{
    private const string BeatmapNotFoundFailureReason = "beatmap_not_found";
    private const string BeatmapsetDownloadFailedFailureReason = "beatmapset_download_failed";
    private static readonly TimeSpan FirstBeatmapsetByteTimeout = TimeSpan.FromSeconds(15);

    private static HttpClient s_HttpClient { get; } = new() { Timeout = TimeSpan.FromSeconds(30) };

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

    public async Task<Result<Stream>> DownloadBeatmapsetAsStream(string beatmapHash)
    {
        Exception? lastException = null;

        foreach (var provider in BeatmapsetsProviders)
        {
            string providerName = provider.GetType().Name;
            var downloadResult = await provider.DownloadBeatmapset(beatmapHash);
            if (!downloadResult.Success)
            {
                lastException = downloadResult.Exception;
                continue;
            }

            var firstByteResult = await EnsureFirstBeatmapsetByteArrives(downloadResult.Output!, providerName);
            if (firstByteResult.Success)
                return firstByteResult;

            lastException = firstByteResult.Exception;
            Logger.LogError(firstByteResult.Exception!, $"{providerName} did not return beatmapset data quickly enough. Trying another provider.");
        }

        return Result<Stream>.FromFailure(lastException ?? new Exception("Failed to download a beatmapset"));
    }

    public async Task<Result<Stream>> DownloadBeatmapsetAsStreamUsingFallbackProvider(string beatmapHash)
    {
        var downloadResult = await FallbackProvider.DownloadBeatmapset(beatmapHash);
        return downloadResult.Success
            ? await EnsureFirstBeatmapsetByteArrives(downloadResult.Output!, FallbackProvider.GetType().Name)
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

    public async Task<Result> SaveBeatmapsetStreamAsFile(RenderPipelineInfo info, Stream beatmapsetStream)
    {
        using (var oszStream = beatmapsetStream)
        {
            MemoryStream oszStreamCopy = new MemoryStream();

            await oszStream.CopyToAsync(oszStreamCopy);
            oszStreamCopy.Position = 0;

            if (oszStreamCopy.Length == 0)
            {
                Logger.LogError($"[JobId:{info.RenderJob!.JobId}] Downloaded beatmapset stream was empty.");
                return Result.FromFailure(new InvalidDataException("The downloaded beatmapset is empty."));
            }

            string tempSuffix = $".download-{Guid.NewGuid():N}";
            string tempBeatmapsetDirectoryPath = info.BeatmapsetDirectoryPath + tempSuffix;
            string tempBeatmapsetOszPath = info.BeatmapsetOszPath + tempSuffix;

            try
            {
                if (info.UseExperimentalRenderer)
                {
                    using FileStream fs = new FileStream(tempBeatmapsetOszPath, FileMode.CreateNew, FileAccess.Write);
                    await oszStreamCopy.CopyToAsync(fs);
                    oszStreamCopy.Position = 0;
                    Logger.Log($"[JobId:{info.RenderJob!.JobId}] Beatmapset archive saved for experimental renderer: {tempBeatmapsetOszPath}");
                }

                ZipFile.ExtractToDirectory(oszStreamCopy, tempBeatmapsetDirectoryPath);
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
                Logger.LogError(downloadResult.Exception!, $"[JobId:{info.RenderJob!.JobId}] {providerName} failed to download beatmapset.");
                continue;
            }

            var firstByteResult = await EnsureFirstBeatmapsetByteArrives(downloadResult.Output!, providerName);
            if (!firstByteResult.Success)
            {
                lastException = firstByteResult.Exception;
                Logger.LogError(firstByteResult.Exception!, $"[JobId:{info.RenderJob!.JobId}] {providerName} did not return beatmapset data quickly enough. Trying another provider.");
                continue;
            }

            var saveResult = await SaveBeatmapsetStreamAsFile(info, firstByteResult.Output!);
            if (saveResult.Success)
                return Result.FromSuccess();

            lastException = saveResult.Exception;
            Logger.LogError(saveResult.Exception!, $"[JobId:{info.RenderJob!.JobId}] {providerName} returned an invalid beatmapset. Trying another provider.");
        }

        return Result.FromFailure(lastException ?? new Exception("Failed to download a valid beatmapset."));
    }

    private static async Task<Result<Stream>> EnsureFirstBeatmapsetByteArrives(Stream stream, string providerName)
    {
        byte[] firstByte = new byte[1];

        try
        {
            int bytesRead = await stream.ReadAsync(firstByte.AsMemory(0, 1)).AsTask().WaitAsync(FirstBeatmapsetByteTimeout);
            if (bytesRead == 0)
            {
                await stream.DisposeAsync();
                return Result<Stream>.FromFailure(new InvalidDataException($"{providerName} returned an empty beatmapset stream."));
            }

            return Result<Stream>.FromSuccess(new PrefixedStream(firstByte[0], stream));
        }
        catch (TimeoutException ex)
        {
            await stream.DisposeAsync();
            return Result<Stream>.FromFailure(new TimeoutException($"{providerName} did not send the first .osz byte within {FirstBeatmapsetByteTimeout.TotalSeconds:0} seconds.", ex));
        }
        catch (Exception ex)
        {
            await stream.DisposeAsync();
            return Result<Stream>.FromFailure(ex);
        }
    }

    private sealed class PrefixedStream(byte firstByte, Stream innerStream) : Stream
    {
        private bool _firstByteRead;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => innerStream.Flush();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count <= 0)
                return 0;

            if (_firstByteRead)
                return innerStream.Read(buffer, offset, count);

            buffer[offset] = firstByte;
            _firstByteRead = true;
            return 1;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.Length <= 0)
                return 0;

            if (_firstByteRead)
                return await innerStream.ReadAsync(buffer, cancellationToken);

            buffer.Span[0] = firstByte;
            _firstByteRead = true;
            return 1;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                innerStream.Dispose();

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await innerStream.DisposeAsync();
            await base.DisposeAsync();
        }
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

            var downloadResult = await DownloadAndSaveValidBeatmapset(info);
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
