namespace ClientRenderer.Models
{
    internal class DanserCredentials
    {
        public string ClientId { get; set; } = string.Empty;
        public string ClientSecret { get; set; } = string.Empty;
        public string AuthType { get; set; } = string.Empty;
        public int CallbackPort { get; set; }
        public string AccessToken { get; set; } = string.Empty;
        public DateTime Expiry { get; set; }
        public string RefreshToken { get; set; } = string.Empty;
    }
}
