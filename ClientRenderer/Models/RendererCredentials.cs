using System.Text.Json.Serialization;

namespace ClientRenderer.Models
{
    internal record RendererCredentials
    {
        [JsonPropertyName("client-id")]
        public int ClientId { get; set; } = -1;

        [JsonPropertyName("client-secret")]
        public string ClientSecret { get; set; } = string.Empty;
    }
}
