using ClientRenderer.Logging;
using Polly;
using Polly.Retry;

namespace ClientRenderer.Utils
{
    internal class HttpRetryHandler(HttpClientHandler handler) : DelegatingHandler(handler)
    {
        private static int MaxRetries = 3;
        private static AsyncRetryPolicy<HttpResponseMessage> RetryPolicy = Policy.Handle<HttpRequestException>()
                                        .Or<TaskCanceledException>()
                                        .OrResult<HttpResponseMessage>(x => !x.IsSuccessStatusCode && x.StatusCode != System.Net.HttpStatusCode.NotFound)
                                        .WaitAndRetryAsync(MaxRetries, retryAttempt => TimeSpan.FromSeconds(3 + Random.Shared.Next(1, 5)),
                                            (hrm, ts) =>
                                            {
                                                if (hrm.Exception != null)
                                                {
                                                    Logger.LogError($"[HttpRetryHandler] An exception occured. Retrying... See logs for details. {hrm.Exception.Message}");
                                                    Logger.LogDebug($"[HttpRetryHandler] An exception occured. Retrying... {hrm.Exception}");
                                                }
                                                else if (hrm.Result != null)
                                                {
                                                    Logger.LogError($"[HttpRetryHandler] An exception occured. Retrying... See logs for details. {hrm.Result.ReasonPhrase}");
                                                    Logger.LogDebug($"[HttpRetryHandler] A request was unsuccessful. Retrying... {hrm.Result}");
                                                }
                                            });
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return RetryPolicy.ExecuteAsync(() => base.SendAsync(request, cancellationToken));
        }
    }
}
