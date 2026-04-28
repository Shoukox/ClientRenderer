using ClientRenderer.Abstractions;
using ClientRenderer.Models;
using ClientRenderer.Utils;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace ClientRenderer.Connection
{
    public class ServerConnection : IServerConnection
    {
        private readonly HttpClient _httpClient = new(new HttpRetryHandler(new HttpClientHandler()));
        private readonly RendererCredentials _rendererCredentials;
        private const int TokenWaitMs = 1000;
        private ClientCredentialsGrantResponse? _lastClientCredentialsGrantResponse = null;
        private DateTime _nextTokenRefreshTime = DateTime.MinValue;

        private int heartbeatIntervalMs = 10_000;
        private readonly Task _sendHeartbeatsTask;

        private readonly CancellationTokenSource _internalCts;
        private readonly CancellationToken _cancellationToken;

        public ServerConnection(string url, RendererCredentials credentials, CancellationToken cancellationToken)
        {
            _httpClient.BaseAddress = new Uri(url);
            _rendererCredentials = credentials;
            _internalCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _cancellationToken = _internalCts.Token;
            _sendHeartbeatsTask = Task.Run(SendHeartbeatWorker, _cancellationToken);
        }

        public async Task<bool> InitializeToken()
        {
            using HttpRequestMessage hrm = new HttpRequestMessage();
            hrm.Method = HttpMethod.Post;
            hrm.RequestUri = new Uri(_httpClient.BaseAddress!, "jwt");
            hrm.Content = JsonContent.Create(new ClientCredentialsGrantRequest
            {
                ClientId = _rendererCredentials.ClientId,
                ClientSecret = _rendererCredentials.ClientSecret,
                GrantType = "client_credentials",
                Scope = "renderer"
            });
            try
            {
                using var response = await _httpClient.SendAsync(hrm, _cancellationToken);
                response.EnsureSuccessStatusCode();

                _lastClientCredentialsGrantResponse = await response.Content.ReadFromJsonAsync<ClientCredentialsGrantResponse>(_cancellationToken);
                if (string.IsNullOrWhiteSpace(_lastClientCredentialsGrantResponse?.AccessToken) || _lastClientCredentialsGrantResponse.ExpiresIn <= 0)
                {
                    LogError("Token response is invalid.");
                    return false;
                }

                _nextTokenRefreshTime = DateTime.Now.AddSeconds(_lastClientCredentialsGrantResponse.ExpiresIn * 0.9); // 90% of the token lifetime
                await SendHeartbeat();
                return true;
            }
            catch (Exception ex)
            {
                LogError($"InitializeToken failed: {ex.Message}");
                return false;
            }
        }

        private async Task SendHeartbeatWorker()
        {
            while (!_cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (_lastClientCredentialsGrantResponse == null)
                    {
                        await Task.Delay(TokenWaitMs, _cancellationToken);
                        continue;
                    }
                    if (_nextTokenRefreshTime - DateTime.Now <= TimeSpan.Zero)
                    {
                        Log("Reinitializing an access token");
                        while (!await InitializeToken())
                        {
                            Log("Error while reinitializing an access token. Retrying...");
                            await Task.Delay(5000, _cancellationToken);
                        }
                    }
                    await SendHeartbeat();

                    Log("A heartbeat has been sent.");
                    await Task.Delay(heartbeatIntervalMs, _cancellationToken);
                }
                catch (HttpRequestException)
                {
                    LogError("Error while doing a request. Retrying in 10 seconds...");
                    await Task.Delay(TimeSpan.FromSeconds(10), _cancellationToken); // wait before retrying on HTTP errors
                }
                catch (Exception ex)
                {
                    LogError(ex.ToString());
                }
            }
        }

        private async Task SendHeartbeat()
        {
            using HttpRequestMessage hrm = new HttpRequestMessage();
            hrm.Method = HttpMethod.Post;
            hrm.RequestUri = new Uri(_httpClient.BaseAddress!, "render/heartbeat");
            hrm.Headers.Authorization = AuthenticationHeaderValue.Parse($"Bearer {_lastClientCredentialsGrantResponse!.AccessToken}");
            using var response = await _httpClient.SendAsync(hrm);
            response.EnsureSuccessStatusCode();
        }

        public async Task<RenderJob?> GetNextRenderJob(int intervalMs = 2000)
        {
            RenderJob? renderJob = null;
            while (!_cancellationToken.IsCancellationRequested)
            {
                try
                {
                    using HttpRequestMessage hrm = new HttpRequestMessage();
                    hrm.Method = HttpMethod.Post;
                    hrm.RequestUri = new Uri(_httpClient.BaseAddress!, "render/get-next-render-job");
                    hrm.Headers.Authorization = AuthenticationHeaderValue.Parse($"Bearer {_lastClientCredentialsGrantResponse!.AccessToken}");
                    using var response = await _httpClient.SendAsync(hrm);
                    if (response.IsSuccessStatusCode)
                    {
                        renderJob = (await response.Content.ReadFromJsonAsync<RenderJob>())!;
                        break;
                    }
                    await Task.Delay(intervalMs, _cancellationToken);
                }
                catch (HttpRequestException)
                {
                    LogError("Error while doing a request. Retrying in 10 seconds...");
                    await Task.Delay(TimeSpan.FromSeconds(10), _cancellationToken); // wait before retrying on HTTP errors
                }
                catch (Exception ex)
                {
                    LogError(ex.ToString());
                }
            }

            return renderJob;
        }

        public async Task<RenderJob?> GetRenderJobInfo(int jobId)
        {
            using HttpRequestMessage hrm = new HttpRequestMessage();
            hrm.Method = HttpMethod.Post;
            hrm.RequestUri = new Uri(_httpClient.BaseAddress!, $"render/get-render-job-info?job-id={jobId}");
            hrm.Headers.Authorization = AuthenticationHeaderValue.Parse($"Bearer {_lastClientCredentialsGrantResponse!.AccessToken}");
            using var response = await _httpClient.SendAsync(hrm, _cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;

            return await response.Content.ReadFromJsonAsync<RenderJob>(_cancellationToken);
        }

        public async Task<byte[]> DownloadReplay(int jobId)
        {
            using HttpRequestMessage hrm = new HttpRequestMessage();
            hrm.Method = HttpMethod.Post;
            hrm.RequestUri = new Uri(_httpClient.BaseAddress!, $"render/download-replay?job-id={jobId}");
            hrm.Headers.Authorization = AuthenticationHeaderValue.Parse($"Bearer {_lastClientCredentialsGrantResponse!.AccessToken}");
            using var response = await _httpClient.SendAsync(hrm);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsByteArrayAsync();
        }

        public async Task<byte[]> DownloadSkin(string skinFileNameHex)
        {
            using HttpRequestMessage hrm = new HttpRequestMessage();
            hrm.Method = HttpMethod.Get;
            hrm.RequestUri = new Uri(_httpClient.BaseAddress!, $"skins/{skinFileNameHex}");
            using var response = await _httpClient.SendAsync(hrm);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsByteArrayAsync();
        }

        public async Task ReportRenderingProgress(int jobId, double progress)
        {
            using HttpRequestMessage hrm = new HttpRequestMessage();
            hrm.Method = HttpMethod.Post;
            hrm.RequestUri = new Uri(_httpClient.BaseAddress!, $"render/report-rendering-progress?job-id={jobId}&progress={progress.ToString(CultureInfo.InvariantCulture)}");
            hrm.Headers.Authorization = AuthenticationHeaderValue.Parse($"Bearer {_lastClientCredentialsGrantResponse!.AccessToken}");
            using var response = await _httpClient.SendAsync(hrm);
            response.EnsureSuccessStatusCode();
        }

        public async Task FinishRendering(int jobId)
        {
            using HttpRequestMessage hrm = new HttpRequestMessage();
            hrm.Method = HttpMethod.Post;
            hrm.RequestUri = new Uri(_httpClient.BaseAddress!, $"render/finish-rendering?job-id={jobId}");
            hrm.Headers.Authorization = AuthenticationHeaderValue.Parse($"Bearer {_lastClientCredentialsGrantResponse!.AccessToken}");
            using var response = await _httpClient.SendAsync(hrm);
            response.EnsureSuccessStatusCode();
        }

        public async Task SetRenderJobMetadata(RenderJob renderJob)
        {
            using HttpRequestMessage hrm = new HttpRequestMessage();
            hrm.Method = HttpMethod.Post;
            hrm.RequestUri = new Uri(_httpClient.BaseAddress!, $"render/set-renderjob-metadata?job-id={renderJob.JobId}");
            hrm.Headers.Authorization = AuthenticationHeaderValue.Parse($"Bearer {_lastClientCredentialsGrantResponse!.AccessToken}");
            hrm.Headers.TryAddWithoutValidation("PlayerName", renderJob.PlayerName);
            hrm.Headers.TryAddWithoutValidation("MapName", renderJob.MapName);
            using var response = await _httpClient.SendAsync(hrm);
            response.EnsureSuccessStatusCode();
        }

        public async Task Failure(int jobId, string reason, bool rerender = true)
        {
            using HttpRequestMessage hrm = new HttpRequestMessage();
            hrm.Method = HttpMethod.Post;
            var encodedReason = Uri.EscapeDataString(reason ?? string.Empty);
            hrm.RequestUri = new Uri(_httpClient.BaseAddress!, $"render/failure?job-id={jobId}&reason={encodedReason}&rerender={rerender}");
            hrm.Headers.Authorization = AuthenticationHeaderValue.Parse($"Bearer {_lastClientCredentialsGrantResponse!.AccessToken}");
            using var response = await _httpClient.SendAsync(hrm);
            response.EnsureSuccessStatusCode();
        }

        public async Task PostVideo(string videoPath, int jobId, int chunkSizeBytes = 5 * 1024 * 1024)
        {
            const int maxRetriesPerChunk = 5;

            var fileInfo = new FileInfo(videoPath);
            long fileSize = fileInfo.Length;
            int totalChunks = (int)Math.Ceiling((double)fileSize / chunkSizeBytes);

            await using var fileStream = new FileStream(videoPath, FileMode.Open, FileAccess.Read);

            for (int chunkIndex = 0; chunkIndex < totalChunks; chunkIndex++)
            {
                int attempts = 0;
                bool uploaded = false;
                while (attempts < maxRetriesPerChunk)
                {
                    attempts++;
                    try
                    {
                        long offset = (long)chunkIndex * chunkSizeBytes;
                        int currentChunkSize = (int)Math.Min(chunkSizeBytes, fileSize - offset);
                        byte[] buffer = new byte[currentChunkSize];

                        fileStream.Seek(offset, SeekOrigin.Begin);
                        int read = await fileStream.ReadAsync(buffer, 0, currentChunkSize, _cancellationToken);

                        using var multipart = new MultipartFormDataContent
                        {
                            { new ByteArrayContent(buffer, 0, read), "file", $"video.part{chunkIndex}.mp4" }
                        };

                        using var hrm = new HttpRequestMessage();
                        hrm.Content = multipart;
                        hrm.Method = HttpMethod.Post;
                        hrm.RequestUri = new Uri(_httpClient.BaseAddress!, $"render/upload-replay-videofile?job-id={jobId}&chunk-index={chunkIndex}&total-chunks={totalChunks}");
                        hrm.Headers.Authorization = AuthenticationHeaderValue.Parse($"Bearer {_lastClientCredentialsGrantResponse!.AccessToken}");

                        using var response = await _httpClient.SendAsync(hrm, _cancellationToken);
                        response.EnsureSuccessStatusCode();

                        Log($"Uploaded chunk {chunkIndex + 1}/{totalChunks}");
                        uploaded = true;
                        break;
                    }
                    catch (Exception ex) when (attempts < maxRetriesPerChunk)
                    {
                        LogError($"Error while uploading chunk {chunkIndex + 1}/{totalChunks}: {ex.Message}. Retry {attempts}/{maxRetriesPerChunk}...");
                        await Task.Delay(1000, _cancellationToken);
                    }
                }

                if (!uploaded)
                {
                    throw new Exception($"Failed to upload chunk {chunkIndex + 1}/{totalChunks} after {maxRetriesPerChunk} attempts.");
                }
            }
        }

        public async Task UploadThumbnail(string thumbnailPath, int jobId)
        {
            using var hrm = new HttpRequestMessage();

            await using var fileStream = new FileStream(thumbnailPath, FileMode.Open, FileAccess.Read);
            using var multipart = new MultipartFormDataContent
            {
                { new StreamContent(fileStream), "file", $"thumbnail.png" }
            };

            hrm.Content = multipart;
            hrm.Method = HttpMethod.Post;
            hrm.RequestUri = new Uri(_httpClient.BaseAddress!, $"thumbnails/upload?job-id={jobId}");
            hrm.Headers.Authorization = AuthenticationHeaderValue.Parse($"Bearer {_lastClientCredentialsGrantResponse!.AccessToken}");

            using var response = await _httpClient.SendAsync(hrm);
            response.EnsureSuccessStatusCode();
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                _internalCts.Cancel();
                await _sendHeartbeatsTask;
            }
            catch (OperationCanceledException)
            {
                // expected during shutdown
            }
            finally
            {
                _httpClient.Dispose();
                _internalCts.Dispose();
            }
        }

        private void Log(string message)
        {
            Console.WriteLine($"\x1b[32m[{DateTime.Now:yyyy-MM-dd HH:mm:ss.ffff}]\e[38;5;198m[Server] \x1b[36m{message}\x1b[0m");
        }

        private void LogError(string message)
        {
            Console.WriteLine($"\x1b[32m[{DateTime.Now:yyyy-MM-dd HH:mm:ss.ffff}]\u001b[38;5;198m[Server] \u001b[31m{message}\x1b[0m");
        }
    }
}
