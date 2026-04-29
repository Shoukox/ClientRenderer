using OsuParsers.Replays;

namespace ClientRenderer.Models
{
    public class RenderPipelineInfo
    {
        public required RenderJob RenderJob { get; set; }
        public bool UseExperimentalRenderer { get; set; } = false;
        public Replay DecodedReplay { get; set; } = default!;
        public byte[] ReplayAsBytes { get; set; } = default!;
        public string ChosenRenderingEncoder { get; set; } = default!;
        public int? BeatmapLength { get; set; } = null;
        public string BeatmapHash { get; set; } = default!;
        public string ReplayPath { get; set; } = default!;
        public string VideoPath { get; set; } = default!;
        public string BeatmapsetOszPath { get; set; } = default!;
        public string BeatmapsetDirectoryPath { get; set; } = default!;
        public string SkinOskPath { get; set; } = default!;
        public string HashedSkinName { get; set; } = default!;
        public long FileTimeNow { get; set; } = DateTime.Now.ToFileTime();
    }
}
