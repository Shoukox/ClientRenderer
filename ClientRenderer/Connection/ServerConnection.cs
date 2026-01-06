using ClientRenderer.Models;
using SharpCompress.Common;
using System;
using System.Globalization;
using System.IO;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using static System.Net.Mime.MediaTypeNames;

namespace ClientRenderer.Connection
{
    internal class ServerConnection
    {
        private HttpClient _httpClient = new HttpClient();
        private RendererCredentials _rendererCredentials;
        private ClientCredentialsGrantResponse? _lastClientCredentialsGrantResponse = null;
        private DateTime _nextTokenRefreshTime = DateTime.MinValue;

        private int heartbeatIntervalMs = 10_000;
        private readonly Task _sendHeartbeatsTask;

        private CancellationToken _cancellationToken;

        public ServerConnection(string url, RendererCredentials credentials, CancellationToken cancellationToken)
        {
            _httpClient.BaseAddress = new Uri(url);
            _rendererCredentials = credentials;
            _cancellationToken = cancellationToken;
            _sendHeartbeatsTask = Task.Run(SendHeartbeat);
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
                using var response = await _httpClient.SendAsync(hrm);
                _lastClientCredentialsGrantResponse = await response.Content.ReadFromJsonAsync<ClientCredentialsGrantResponse>();
                _nextTokenRefreshTime = DateTime.Now.AddSeconds(_lastClientCredentialsGrantResponse!.ExpiresIn * 0.9); // 90% of the token lifetime
                return true;
            }
            catch
            {
                return false;
            }
        }

        private async Task SendHeartbeat()
        {
            while (!_cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (_lastClientCredentialsGrantResponse == null)
                    {
                        await Task.Delay(3000);
                        continue;
                    }
                    if (_nextTokenRefreshTime - DateTime.Now <= TimeSpan.Zero)
                    {
                        Log("Reinitializing an access token");
                        while (!await InitializeToken())
                        {
                            Log("Error while reinitializing an access token. Retrying...");
                            await Task.Delay(5000);
                        }
                    }
                    using HttpRequestMessage hrm = new HttpRequestMessage();
                    hrm.Method = HttpMethod.Post;
                    hrm.RequestUri = new Uri(_httpClient.BaseAddress!, "render/heartbeat");
                    hrm.Headers.Authorization = AuthenticationHeaderValue.Parse($"Bearer {_lastClientCredentialsGrantResponse.AccessToken}");
                    using var response = await _httpClient.SendAsync(hrm);
                    response.EnsureSuccessStatusCode();

                    Log("Heartbeat was sent.");
                    await Task.Delay(heartbeatIntervalMs);
                }
                catch (HttpRequestException)
                {
                    Log("Error while doing a request. Retrying in 10 seconds...");
                    await Task.Delay(TimeSpan.FromSeconds(10), _cancellationToken); // wait before retrying on HTTP errors
                }
                catch (Exception ex)
                {
                    Log(ex.ToString());
                }
            }
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
                    await Task.Delay(intervalMs);
                }
                catch (HttpRequestException)
                {
                    Log("Error while doing a request. Retrying in 10 seconds...");
                    await Task.Delay(TimeSpan.FromSeconds(10), _cancellationToken); // wait before retrying on HTTP errors
                }
                catch (Exception ex)
                {
                    Log(ex.ToString());
                }
            }

            return renderJob;
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

        public async Task Failure(int jobId, string reason, bool rerender = true)
        {
            using HttpRequestMessage hrm = new HttpRequestMessage();
            hrm.Method = HttpMethod.Post;
            hrm.RequestUri = new Uri(_httpClient.BaseAddress!, $"render/failure?job-id={jobId}&reason={reason}&rerender={rerender}");
            hrm.Headers.Authorization = AuthenticationHeaderValue.Parse($"Bearer {_lastClientCredentialsGrantResponse!.AccessToken}");
            using var response = await _httpClient.SendAsync(hrm);
            response.EnsureSuccessStatusCode();
        }

        public async Task PostVideo(string videoPath, int jobId, int chunkSizeBytes = 50 * 1024 * 1024)
        {
            var fileInfo = new FileInfo(videoPath);
            long fileSize = fileInfo.Length;
            int totalChunks = (int)Math.Ceiling((double)fileSize / chunkSizeBytes);

            await using var fileStream = new FileStream(videoPath, FileMode.Open, FileAccess.Read);

            for (int chunkIndex = 0; chunkIndex < totalChunks; chunkIndex++)
            {
                long offset = (long)chunkIndex * chunkSizeBytes;
                int currentChunkSize = (int)Math.Min(chunkSizeBytes, fileSize - offset);
                byte[] buffer = new byte[currentChunkSize];

                fileStream.Seek(offset, SeekOrigin.Begin);
                int read = await fileStream.ReadAsync(buffer, 0, currentChunkSize);

                using var multipart = new MultipartFormDataContent
                {
                    { new ByteArrayContent(buffer, 0, read), "file", $"video.part{chunkIndex}.mp4" }
                };

                using var hrm = new HttpRequestMessage();
                hrm.Content = multipart;
                hrm.Method = HttpMethod.Post;
                hrm.RequestUri = new Uri(_httpClient.BaseAddress!, $"render/upload-replay-videofile?job-id={jobId}&chunk-index={chunkIndex}&total-chunks={totalChunks}");
                hrm.Headers.Authorization = AuthenticationHeaderValue.Parse($"Bearer {_lastClientCredentialsGrantResponse!.AccessToken}");

                using var response = await _httpClient.SendAsync(hrm);
                response.EnsureSuccessStatusCode();

                Log($"Uploaded chunk {chunkIndex + 1}/{totalChunks}");
            }
        }

        public async Task PostImage(string screenshotPath, int jobId)
        {
            using var hrm = new HttpRequestMessage();
            
            await using var fileStream = new FileStream(screenshotPath, FileMode.Open, FileAccess.Read);
            using var multipart = new MultipartFormDataContent
            {
                { new StreamContent(fileStream), "file", $"image.png" }
            };

            hrm.Content = multipart;
            hrm.Method = HttpMethod.Post;
            hrm.RequestUri = new Uri(_httpClient.BaseAddress!, $"images/upload-image?job-id={jobId}");
            hrm.Headers.Authorization = AuthenticationHeaderValue.Parse($"Bearer {_lastClientCredentialsGrantResponse!.AccessToken}");

            using var response = await _httpClient.SendAsync(hrm);
            response.EnsureSuccessStatusCode();
        }

        private void Log(string message)
        {
            Console.WriteLine($"\x1b[32m[{DateTime.Now:yyyy-MM-dd HH:mm:ss.ffff}]\x1b[31m[Server] \x1b[36m{message}\x1b[0m");
        }

        internal class ClientCredentialsGrantRequest
        {
            [JsonPropertyName("client_id")] public required int ClientId { get; set; }

            [JsonPropertyName("client_secret")] public required string ClientSecret { get; set; }

            [JsonPropertyName("grant_type")] public required string GrantType { get; set; }

            [JsonPropertyName("scope")] public required string Scope { get; set; }
        }

        internal class ClientCredentialsGrantResponse
        {
            [JsonPropertyName("token_type")] public string? TokenType { get; set; }

            [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }

            [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        }
    }
}
