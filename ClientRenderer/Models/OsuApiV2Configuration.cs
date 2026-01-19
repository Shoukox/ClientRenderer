namespace ClientRenderer.Models;

public record OsuApiV2Configuration
{
    public int ClientId { get; init; } = -1;
    public string ClientSecret { get; init; } = string.Empty;
}