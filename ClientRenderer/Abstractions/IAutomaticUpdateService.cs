namespace ClientRenderer.Abstractions;

public interface IAutomaticUpdateService
{
    Task<bool> CheckForUpdatesAsync(CancellationToken cancellationToken = default);
}
