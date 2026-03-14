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
                                                    LogError($"An exception occured. Retrying...\n{hrm.Exception}");
                                                }
                                                else if (hrm.Result != null)
                                                {
                                                    LogError($"A request was unsuccessful. {hrm.Result.ReasonPhrase}. Retrying...");
                                                }
                                            });
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return RetryPolicy.ExecuteAsync(() => base.SendAsync(request, cancellationToken));
        }
        private static void LogError(string message)
        {
            Console.WriteLine($"\x1b[32m[{DateTime.Now:yyyy-MM-dd HH:mm:ss.ffff}]\u001b[38;5;198m[HttpRetryHandler] \u001b[31m{message}\x1b[0m");
        }
    }
}
