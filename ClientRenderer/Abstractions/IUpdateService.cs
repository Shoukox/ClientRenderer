namespace ClientRenderer.Abstractions;

public interface IUpdateService
{
    Task CheckForUpdatesAsync(string[] args);
}
