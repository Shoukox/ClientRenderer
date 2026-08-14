using ClientRenderer.Abstractions;
using ClientRenderer.Logging;
using ClientRenderer.Models;
using ClientRenderer.Startup;
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
        private const string ClientVersionHeader = "X-Client-Renderer-Version";
        private readonly string _clientVersion = ClientRendererVersion.Current;
        private readonly SemaphoreSlim _heartbeatRequestGate = new(1, 1);

        private readonly CancellationTokenSource _internalCts;
        private readonly CancellationToken _cancellationToken;
        private readonly object _heartbeatSync = new();
        private readonly object _updateRequestSync = new();
        private int _consecutiveHeartbeatFailures;
        private bool _serverUpdateRequested;
        private string? _serverRequestedLatestVersion;

        public event Action<HeartbeatStatus>? HeartbeatStatusChanged;

        public ServerConnection(string url, RendererCredentials credentials, CancellationToken cancellationToken)
        {
            _httpClient.BaseAddress = new Uri(url);
            _rendererCredentials = credentials;
            _internalCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _cancellationToken = _internalCts.Token;
            _sendHeartbeatsTask = Task.Run(SendHeartbeatWorker, _cancellationToken);
            Logger.Log($"Server connection initialized. Base URL: {_httpClient.BaseAddress}. Client version: {_clientVersion}");
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
                Logger.Log($"Access token initialized. Refresh scheduled at {_nextTokenRefreshTime:O}.");
                await SendHeartbeat();
                return true;
            }
            catch (Exception ex)
            {
                notifyHeartbeatFailure();
                Logger.LogError(ex, "Failed to initialize access token.");
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
                        Logger.Log("Reinitializing access token.");
                        while (!await InitializeToken())
                        {
                            notifyHeartbeatFailure();
                            Logger.LogWarning("Failed to reinitialize access token. Retrying...");
                            await Task.Delay(5000, _cancellationToken);
                        }
                    }
                    await SendHeartbeat();
                    await Task.Delay(heartbeatIntervalMs, _cancellationToken);
                }
                catch (HttpRequestException ex)
                {
                    notifyHeartbeatFailure();
                    Logger.LogError(ex, "Heartbeat request failed. Retrying in 10 seconds...");
                    await Task.Delay(TimeSpan.FromSeconds(10), _cancellationToken);
                }
                catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    notifyHeartbeatFailure();
                    Logger.LogError(ex, "Unexpected heartbeat worker failure.");
                }
            }
        }

        private async Task SendHeartbeat()
        {
            await _heartbeatRequestGate.WaitAsync(_cancellationToken);
            try
            {
                using HttpRequestMessage hrm = new HttpRequestMessage
                {
                    Method = HttpMethod.Post,
                    RequestUri = new Uri(_httpClient.BaseAddress!, "render/heartbeat")
                };
                hrm.Headers.Authorization = AuthenticationHeaderValue.Parse($"Bearer {_lastClientCredentialsGrantResponse!.AccessToken}");
                hrm.Headers.TryAddWithoutValidation(ClientVersionHeader, _clientVersion);

                using var response = await _httpClient.SendAsync(hrm, _cancellationToken);
                response.EnsureSuccessStatusCode();

                await ProcessHeartbeatResponseAsync(response);

                notifyHeartbeatSuccess();
            }
            finally
            {
                _heartbeatRequestGate.Release();
            }
        }

        private async Task ProcessHeartbeatResponseAsync(HttpResponseMessage response)
        {
            string responseBody = await response.Content.ReadAsStringAsync(_cancellationToken);
            if (string.IsNullOrWhiteSpace(responseBody))
                return;

            RendererHeartbeatResponse? heartbeatResponse;
            try
            {
                heartbeatResponse = System.Text.Json.JsonSerializer.Deserialize<RendererHeartbeatResponse>(responseBody);
            }
            catch (System.Text.Json.JsonException exception)
            {
                // Older servers return an empty 200 response. A malformed
                // optional response must not make a healthy renderer offline.
                Logger.LogWarning($"Could not parse the optional heartbeat response: {exception.Message}");
                return;
            }

            lock (_updateRequestSync)
            {
                _serverUpdateRequested = heartbeatResponse?.UpdateRequired == true;
                _serverRequestedLatestVersion = heartbeatResponse?.LatestVersion;
            }

            if (heartbeatResponse?.UpdateRequired == true)
            {
                Logger.LogWarning(
                    $"Server requested a ClientRenderer update. Current version: {_clientVersion}; " +
                    $"latest version: {heartbeatResponse.LatestVersion ?? "unknown"}. It will be applied when idle.");
            }
        }

        public bool TryConsumeServerUpdateRequest(out string? latestVersion)
        {
            lock (_updateRequestSync)
            {
                if (!_serverUpdateRequested)
                {
                    latestVersion = null;
                    return false;
                }

                _serverUpdateRequested = false;
                latestVersion = _serverRequestedLatestVersion;
                return true;
            }
        }

        public async Task<RenderJob?> GetNextRenderJob(int intervalMs = 2000)
        {
            RenderJob? renderJob = null;
            while (!_cancellationToken.IsCancellationRequested)
            {
                // Do not claim a new job after the server has requested an
                // update. RenderWorker will consume the request at this idle
                // boundary and let Velopack restart the client.
                if (HasPendingServerUpdateRequest())
                    return null;

                try
                {
                    using HttpRequestMessage hrm = new HttpRequestMessage();
                    hrm.Method = HttpMethod.Post;
                    hrm.RequestUri = new Uri(_httpClient.BaseAddress!, "render/get-next-render-job");
                    hrm.Headers.Authorization = AuthenticationHeaderValue.Parse($"Bearer {_lastClientCredentialsGrantResponse!.AccessToken}");
                    using var response = await _httpClient.SendAsync(hrm, _cancellationToken);
                    if (response.IsSuccessStatusCode)
                    {
                        if (HasPendingServerUpdateRequest())
                            return null;

                        renderJob = (await response.Content.ReadFromJsonAsync<RenderJob>(_cancellationToken))!;
                        break;
                    }

                    if (response.StatusCode != HttpStatusCode.NotFound &&
                        response.StatusCode != HttpStatusCode.Conflict &&
                        response.StatusCode != HttpStatusCode.BadRequest &&
                        response.StatusCode != HttpStatusCode.UpgradeRequired)
                    {
                        var responseBody = await TryReadBodyAsync(response);
                        Logger.LogError($"GetNextRenderJob returned {(int)response.StatusCode} {response.StatusCode}. {responseBody}");
                    }

                    if (HasPendingServerUpdateRequest())
                        return null;

                    await Task.Delay(intervalMs, _cancellationToken);
                }
                catch (HttpRequestException ex)
                {
                    Logger.LogError(ex, "Failed to get the next render job. Retrying in 10 seconds...");
                    await Task.Delay(TimeSpan.FromSeconds(10), _cancellationToken);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Unexpected error while getting the next render job.");
                }
            }

            return renderJob;
        }

        private bool HasPendingServerUpdateRequest()
        {
            lock (_updateRequestSync)
                return _serverUpdateRequested;
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
            Logger.Log($"[JobId:{jobId}] Downloading replay from server.");
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
            Logger.Log($"Downloading skin file from server: {skinFileNameHex}");
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
            Logger.LogWarning($"[JobId:{jobId}] Reporting render failure to server. Reason: {reason}. Rerender: {rerender}.");
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
            Logger.Log($"[JobId:{jobId}] Uploading video file. Size: {fileSize} bytes. Chunks: {totalChunks}.");

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
                        Logger.LogError(ex, $"[JobId:{jobId}] Failed to upload chunk {chunkIndex + 1}/{totalChunks}. Retry {attempts}/{maxRetriesPerChunk}...");
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
            Logger.Log($"[JobId:{jobId}] Uploading thumbnail: {thumbnailPath}");
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
                Logger.Log("Heartbeat worker was canceled during shutdown.");
            }
            finally
            {
                notifyHeartbeatFailure();
                _httpClient.Dispose();
                _heartbeatRequestGate.Dispose();
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
            catch (Exception ex)
            {
                Logger.LogWarning($"Failed to read response body: {ex.Message}");
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
