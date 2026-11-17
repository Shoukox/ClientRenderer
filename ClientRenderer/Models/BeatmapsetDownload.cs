namespace ClientRenderer.Models;

/// <summary>
/// An opened beatmapset download: the response body stream and its declared size, if any.
/// Disposing it closes the underlying HTTP response.
/// </summary>
public sealed record BeatmapsetDownload(Stream Content, long? ContentLength) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}
