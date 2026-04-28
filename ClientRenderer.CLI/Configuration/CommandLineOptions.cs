using CommandLine;

namespace ClientRenderer.CLI.Configuration
{
    public class CommandLineOptions
    {
        [Option('s', "server", Required = true, HelpText = "Set the upload server. Example: http://localhost:5000")]
        public string? ServerUrl { get; set; }

        [Option('e', "encoder", Required = false, HelpText = "Set the video encoder. Available: h264_nvenc, av1_nvenc or libx264 for cpu encoding. Defaults to h264_nvenc")]
        public string Encoder { get; set; } = "h264_nvenc";
    }
}
