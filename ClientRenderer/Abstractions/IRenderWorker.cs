namespace ClientRenderer.Abstractions;

public interface IRenderWorker
{
    Task RunAsync(CancellationToken cancellationToken);
    public event Action<bool>? RenderingStatus;
}
