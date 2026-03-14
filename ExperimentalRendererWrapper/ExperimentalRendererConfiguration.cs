using ExperimentalRendererWrapper.Configuration;
using Newtonsoft.Json;

namespace ExperimentalRendererWrapper
{
    public class ExperimentalRendererConfiguration
    {
        [JsonProperty("record_options")] public RecordOptionsObject RecordOptions = new();
        [JsonProperty("ffmpeg_options")] public FFmpegOptionsObject FFmpegOptions = new();
        [JsonProperty("output_options")] public OutputOptionsObject OutputOptions = new();
        [JsonProperty("game_settings")] public GameSettings GameSettings = new();
    }
}
