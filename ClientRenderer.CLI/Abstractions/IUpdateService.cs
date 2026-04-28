namespace ClientRenderer.CLI.Abstractions;

public interface IUpdateService
{
    Task CheckForUpdatesAsync(string[] args);
}
