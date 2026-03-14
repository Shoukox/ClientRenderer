namespace ClientRenderer.Models
{
    public record BeatmapsetInfo
    {
        public int BeatmapsetId { get; set; }
        public int? TotalLength { get; set; } = null;
    }
}
