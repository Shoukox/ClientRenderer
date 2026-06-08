using ClientRenderer.Abstractions;
using ClientRenderer.Logging;
using ClientRenderer.Models;
using ClientRenderer.Utils;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace ClientRenderer.Connection
{
    public readonly record struct HeartbeatStatus(bool IsOnline, int ConsecutiveFailures);

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
        private readonly object _heartbeatSync = new();
        private int _consecutiveHeartbeatFailures;

        public event Action<HeartbeatStatus>? HeartbeatStatusChanged;

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
                    notifyHeartbeatFailure();
                    Logger.LogError("Token response is invalid.");
                    return false;
                }

                _nextTokenRefreshTime = DateTime.Now.AddSeconds(_lastClientCredentialsGrantResponse.ExpiresIn * 0.9);
                await SendHeartbeat();
                return true;
            }
            catch (Exception ex)
            {
                notifyHeartbeatFailure();
                Logger.LogError($"InitializeToken failed: {ex.Message}");
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
                        Logger.Log("Reinitializing an access token");
                        while (!await InitializeToken())
                        {
                            notifyHeartbeatFailure();
                            Logger.Log("Error while reinitializing an access token. Retrying...");
                            await Task.Delay(5000, _cancellationToken);
                        }
                    }
                    await SendHeartbeat();
                    await Task.Delay(heartbeatIntervalMs, _cancellationToken);
                }
                catch (HttpRequestException)
                {
                    notifyHeartbeatFailure();
                    Logger.LogError("Error while doing a request. Retrying in 10 seconds...");
                    await Task.Delay(TimeSpan.FromSeconds(10), _cancellationToken);
                }
                catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    notifyHeartbeatFailure();
                    Logger.LogError(ex.ToString());
                }
            }
        }

        private async Task SendHeartbeat()
        {
            using HttpRequestMessage hrm = new HttpRequestMessage();
            hrm.Method = HttpMethod.Post;
            hrm.RequestUri = new Uri(_httpClient.BaseAddress!, "render/heartbeat");
            hrm.Headers.Authorization = AuthenticationHeaderValue.Parse($"Bearer {_lastClientCredentialsGrantResponse!.AccessToken}");
            using var response = await _httpClient.SendAsync(hrm, _cancellationToken);
            response.EnsureSuccessStatusCode();
            notifyHeartbeatSuccess();
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
                    using var response = await _httpClient.SendAsync(hrm, _cancellationToken);
                    if (response.IsSuccessStatusCode)
                    {
                        renderJob = (await response.Content.ReadFromJsonAsync<RenderJob>(_cancellationToken))!;
                        break;
                    }

                    if (response.StatusCode != HttpStatusCode.NotFound && response.StatusCode != HttpStatusCode.Conflict && response.StatusCode != HttpStatusCode.BadRequest)
                    {
                        var responseBody = await TryReadBodyAsync(response);
                        Logger.LogError($"GetNextRenderJob returned {(int)response.StatusCode} {response.StatusCode}. {responseBody}");
                    }

                    await Task.Delay(intervalMs, _cancellationToken);
                }
                catch (HttpRequestException)
                {
                    Logger.LogError("Error while doing a request. Retrying in 10 seconds...");
                    await Task.Delay(TimeSpan.FromSeconds(10), _cancellationToken);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex.ToString());
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
            using var response = await _httpClient.SendAsync(hrm, _cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsByteArrayAsync(_cancellationToken);
        }

        public async Task<byte[]> DownloadSkin(string skinFileNameHex)
        {
            using HttpRequestMessage hrm = new HttpRequestMessage();
            hrm.Method = HttpMethod.Get;
            hrm.RequestUri = new Uri(_httpClient.BaseAddress!, $"skins/{skinFileNameHex}");
            using var response = await _httpClient.SendAsync(hrm, _cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsByteArrayAsync(_cancellationToken);
        }

        public async Task ReportRenderingProgress(int jobId, double progress)
        {
            using HttpRequestMessage hrm = new HttpRequestMessage();
            hrm.Method = HttpMethod.Post;
            hrm.RequestUri = new Uri(_httpClient.BaseAddress!, $"render/report-rendering-progress?job-id={jobId}&progress={progress.ToString(CultureInfo.InvariantCulture)}");
            hrm.Headers.Authorization = AuthenticationHeaderValue.Parse($"Bearer {_lastClientCredentialsGrantResponse!.AccessToken}");
            using var response = await _httpClient.SendAsync(hrm, _cancellationToken);
            if (await HandleExpectedRendererStateResponseAsync(response, "ReportRenderingProgress", jobId))
            {
                return;
            }

            response.EnsureSuccessStatusCode();
        }

        public async Task FinishRendering(int jobId)
        {
            using HttpRequestMessage hrm = new HttpRequestMessage();
            hrm.Method = HttpMethod.Post;
            hrm.RequestUri = new Uri(_httpClient.BaseAddress!, $"render/finish-rendering?job-id={jobId}");
            hrm.Headers.Authorization = AuthenticationHeaderValue.Parse($"Bearer {_lastClientCredentialsGrantResponse!.AccessToken}");
            using var response = await _httpClient.SendAsync(hrm, _cancellationToken);
            if (await HandleExpectedRendererStateResponseAsync(response, "FinishRendering", jobId))
            {
                return;
            }

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
            hrm.Headers.TryAddWithoutValidation("Duration", renderJob.VideoDuration.ToString());
            using var response = await _httpClient.SendAsync(hrm, _cancellationToken);
            if (await HandleExpectedRendererStateResponseAsync(response, "SetRenderJobMetadata", renderJob.JobId))
            {
                return;
            }

            response.EnsureSuccessStatusCode();
        }

        public async Task Failure(int jobId, string reason, bool rerender = true)
        {
            using HttpRequestMessage hrm = new HttpRequestMessage();
            hrm.Method = HttpMethod.Post;
            var encodedReason = Uri.EscapeDataString(reason ?? string.Empty);
            hrm.RequestUri = new Uri(_httpClient.BaseAddress!, $"render/failure?job-id={jobId}&reason={encodedReason}&rerender={rerender}");
            hrm.Headers.Authorization = AuthenticationHeaderValue.Parse($"Bearer {_lastClientCredentialsGrantResponse!.AccessToken}");
            using var response = await _httpClient.SendAsync(hrm, _cancellationToken);
            if (await HandleExpectedRendererStateResponseAsync(response, "Failure", jobId))
            {
                return;
            }

            response.EnsureSuccessStatusCode();
        }

        public async Task PostVideo(string videoPath, int jobId, int chunkSizeBytes = 5 * 1024 * 1024)
        {
            const int maxRetriesPerChunk = 5;

            FileInfo fileInfo = new FileInfo(videoPath);
            long fileSize = fileInfo.Length;
            int totalChunks = (int)Math.Ceiling((double)fileSize / chunkSizeBytes);

            await using FileStream fileStream = new FileStream(videoPath, FileMode.Open, FileAccess.Read);

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

                        using MultipartFormDataContent multipart = new MultipartFormDataContent
                        {
                            { new ByteArrayContent(buffer, 0, read), "file", $"video.part{chunkIndex}.mp4" }
                        };

                        using HttpRequestMessage hrm = new HttpRequestMessage();
                        hrm.Content = multipart;
                        hrm.Method = HttpMethod.Post;
                        hrm.RequestUri = new Uri(_httpClient.BaseAddress!, $"render/upload-replay-videofile?job-id={jobId}&chunk-index={chunkIndex}&total-chunks={totalChunks}");
                        hrm.Headers.Authorization = AuthenticationHeaderValue.Parse($"Bearer {_lastClientCredentialsGrantResponse!.AccessToken}");

                        using var response = await _httpClient.SendAsync(hrm, _cancellationToken);
                        response.EnsureSuccessStatusCode();

                        Logger.Log($"Uploaded chunk {chunkIndex + 1}/{totalChunks}");
                        uploaded = true;
                        break;
                    }
                    catch (Exception ex) when (attempts < maxRetriesPerChunk)
                    {
                        Logger.LogError($"Error while uploading chunk {chunkIndex + 1}/{totalChunks}: {ex.Message}. Retry {attempts}/{maxRetriesPerChunk}...");
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
            using HttpRequestMessage hrm = new HttpRequestMessage();

            await using FileStream fileStream = new FileStream(thumbnailPath, FileMode.Open, FileAccess.Read);
            using MultipartFormDataContent multipart = new MultipartFormDataContent
            {
                { new StreamContent(fileStream), "file", $"thumbnail.jpg" }
            };

            hrm.Content = multipart;
            hrm.Method = HttpMethod.Post;
            hrm.RequestUri = new Uri(_httpClient.BaseAddress!, $"thumbnails/upload?job-id={jobId}");
            hrm.Headers.Authorization = AuthenticationHeaderValue.Parse($"Bearer {_lastClientCredentialsGrantResponse!.AccessToken}");

            using var response = await _httpClient.SendAsync(hrm, _cancellationToken);
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
            }
            finally
            {
                notifyHeartbeatFailure();
                _httpClient.Dispose();
                _internalCts.Dispose();
            }
        }

        private static bool IsExpectedRendererStateStatus(HttpStatusCode statusCode) => statusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Conflict;

        private static async Task<string> TryReadBodyAsync(HttpResponseMessage response)
        {
            try
            {
                return (await response.Content.ReadAsStringAsync()).Trim();
            }
            catch
            {
                return string.Empty;
            }
        }

        private static async Task<bool> HandleExpectedRendererStateResponseAsync(HttpResponseMessage response, string operationName, int jobId)
        {
            if (response.IsSuccessStatusCode || !IsExpectedRendererStateStatus(response.StatusCode))
            {
                return false;
            }

            var responseBody = await TryReadBodyAsync(response);
            Logger.Log($"[JobId:{jobId}] {operationName} skipped because server returned {(int)response.StatusCode} {response.StatusCode}. {responseBody}");
            return true;
        }

        private void notifyHeartbeatSuccess()
        {
            lock (_heartbeatSync)
            {
                _consecutiveHeartbeatFailures = 0;
                HeartbeatStatusChanged?.Invoke(new HeartbeatStatus(true, 0));
            }
        }

        private void notifyHeartbeatFailure()
        {
            int failures;
            lock (_heartbeatSync)
            {
                _consecutiveHeartbeatFailures++;
                failures = _consecutiveHeartbeatFailures;
            }

            HeartbeatStatusChanged?.Invoke(new HeartbeatStatus(false, failures));
        }
    }
}
