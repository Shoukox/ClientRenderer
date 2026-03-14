using ClientRenderer.Models;

namespace ClientRenderer.Startup;

public sealed record AppConfiguration
{
    public required string SettingsDirectory { get; init; }
    public required string OsuSessionCookie { get; init; }
    public required OsuApiV2Configuration OsuApiV2Configuration { get; init; }
    public required RendererCredentials RendererCredentials { get; init; }
}
