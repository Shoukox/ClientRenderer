using Newtonsoft.Json;

namespace ExperimentalRendererWrapper.Configuration
{
    public class OutputOptionsObject
    {
        [JsonProperty("pixel_format")] public string PixelFormat = "RGB";
    }
}
