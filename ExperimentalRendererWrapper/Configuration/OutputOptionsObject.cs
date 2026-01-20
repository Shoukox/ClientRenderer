using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Text;

namespace ExperimentalRendererWrapper.Configuration
{
    public class OutputOptionsObject
    {
        [JsonProperty("pixel_format")] public string PixelFormat = "RGB";
    }
}
