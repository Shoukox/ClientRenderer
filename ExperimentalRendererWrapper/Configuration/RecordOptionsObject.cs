using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Text;

namespace ExperimentalRendererWrapper.Configuration
{
    public class RecordOptionsObject
    {
        [JsonProperty("fps")] public int FrameRate = 60;
        [JsonProperty("resolution")] public string Resolution = "1280x720";
        [JsonProperty("renderer")] public string Renderer = "Auto";
    }
}
