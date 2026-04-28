namespace ClientRenderer.GUI.Configuration
{
    public sealed class Settings
    {
        public bool RunOnSystemStartup { get; set; }
        public bool MinimizeInsteadOfClosing { get; set; } = true;
        public string DefaultEncoder { get; set; } = "h264_nvenc";
        public string ServerUrl { get; set; } = "https://sosubot.shoukko.de";
        public string Language { get; set; } = "en";
    }
}
