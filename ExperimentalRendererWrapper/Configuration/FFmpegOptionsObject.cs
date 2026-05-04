using Newtonsoft.Json;

namespace ExperimentalRendererWrapper.Configuration
{
    public class FFmpegOptionsObject
    {
        [JsonProperty("mode")] public string Mode = "Pipe";
        [JsonProperty("libraries_path")] public string LibrariesPath = string.Empty;
        [JsonProperty("ffmpeg_executable")] public string Executable = "ffmpeg";
        [JsonProperty("video_encoder")] public string VideoEncoder = "h264_nvenc";
        [JsonProperty("video_encoder_preset")] public string VideoEncoderPreset = "ultrafast";
        [JsonProperty("video_encoder_bitrate")] public string VideoEncoderBitrate = "1M";
    }
}
