namespace ClientRenderer.Helpers
{
    public static class BrowserHelper
    {
        public static void SimulateBrowser(HttpRequestMessage httpRequest)
        {
            httpRequest.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
                                                   "AppleWebKit/537.36 (KHTML, like Gecko) " +
                                                   "Chrome/115.0.0.0 Safari/537.36");
            httpRequest.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            httpRequest.Headers.Add("Referer", "https://catboy.best/");
        }
    }
}
