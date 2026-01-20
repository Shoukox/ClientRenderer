using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Text;

namespace ExperimentalRendererWrapper.Configuration
{
    public class FFmpegOptionsObject
    {
        [JsonProperty("mode")] public string Mode = "Pipe";
        [JsonProperty("libraries_path")] public string LibrariesPath = string.Empty;
        [JsonProperty("ffmpeg_executable")] public string Executable = "ffmpeg";
        [JsonProperty("video_encoder")] public string VideoEncoder = "h264_nvenc";
        [JsonProperty("video_encoder_preset")] public string VideoEncoderPreset = "p2";
        [JsonProperty("video_encoder_bitrate")] public string VideoEncoderBitrate = "1M";
    }
}
