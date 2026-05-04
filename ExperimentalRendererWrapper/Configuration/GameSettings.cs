using Newtonsoft.Json;

namespace ExperimentalRendererWrapper.Configuration
{
    public class GameSettings
    {
        [JsonProperty("skip_intro")] public bool SkipIntro = false;
        [JsonProperty("background_dim")] public double BackgroundDim = 0.75;
        [JsonProperty("show_storyboard_or_video")] public bool ShowStoryboard = true;
        [JsonProperty("use_beatmap_hitsounds")] public bool BeatmapHitsounds = false;
        [JsonProperty("use_beatmap_skin")] public bool BeatmapSkin = false;
        [JsonProperty("use_beatmap_colors")] public bool BeatmapColors = false;
        [JsonProperty("music_volume")] public double VolumeMusic = 0.6;
        [JsonProperty("effects_volume")] public double VolumeEffects = 0.3;
        [JsonProperty("master_volume")] public double VolumeMaster = 0.6;
        [JsonProperty("cursor_size")] public double CursorSize = 1.0;
        [JsonProperty("scroll_speed")] public double ManiaScrollSpeed = 25.0;
        [JsonProperty("scroll_direction")] public string ManiaScrollDirectionUp = "down";
    }
}
