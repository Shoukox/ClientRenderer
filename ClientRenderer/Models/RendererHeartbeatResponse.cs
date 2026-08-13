using System.Text.Json.Serialization;

namespace ClientRenderer.Models;

public sealed record RendererHeartbeatResponse(
    [property: JsonPropertyName("updateRequired")] bool UpdateRequired,
    [property: JsonPropertyName("latestVersion")] string? LatestVersion);
