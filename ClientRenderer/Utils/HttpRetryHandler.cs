using ClientRenderer.Logging;
using Polly;
using Polly.Retry;

namespace ClientRenderer.Utils
{
    internal class HttpRetryHandler(HttpClientHandler handler) : DelegatingHandler(handler)
    {
        private const int MaxRetries = 3;
        private static readonly AsyncRetryPolicy<HttpResponseMessage> RetryPolicy = Policy.Handle<HttpRequestException>()
                                        .Or<TaskCanceledException>()
                                        .OrResult<HttpResponseMessage>(x => !x.IsSuccessStatusCode && x.StatusCode != System.Net.HttpStatusCode.NotFound)
                                        .WaitAndRetryAsync(MaxRetries, retryAttempt => TimeSpan.FromSeconds(3 + Random.Shared.Next(1, 5)),
                                            (hrm, ts) =>
                                            {
                                                if (hrm.Exception != null)
                                                {
                                                    Logger.LogError(hrm.Exception, "[HttpRetryHandler] An exception occurred. Retrying...");
                                                }
                                                else if (hrm.Result != null)
                                                {
                                                    Logger.LogDebug($"[HttpRetryHandler] Request returned {(int)hrm.Result.StatusCode} {hrm.Result.StatusCode}. Retrying...");
                                                }
                                            });
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return RetryPolicy.ExecuteAsync(() => base.SendAsync(request, cancellationToken));
        }
    }
}
