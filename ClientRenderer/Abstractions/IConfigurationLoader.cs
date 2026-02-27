using ClientRenderer.Startup;

namespace ClientRenderer.Abstractions;

public interface IConfigurationLoader
{
    Task<AppConfiguration> LoadAsync();
}
