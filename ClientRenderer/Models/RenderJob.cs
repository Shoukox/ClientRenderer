using static DanserWrapper.DanserGo;

namespace ClientRenderer.Models
{
    public record RenderJob
    {
        public int JobId { get; set; }
        public string VideoUri { get; set; } = "";
        public string VideoLocalPath { get; set; } = "";
        public string PlayerName { get; set; } = "Unknown";
        public string MapName { get; set; } = "Unknown";
        public string VideoThumbnailUri { get; set; } = "";
        public string VideoThumbnailLocalPath { get; set; } = "";
        public string ReplayPath { get; set; } = "";
        public DateTime RequestedAt { get; set; }
        public string RequestedBy { get; set; } = null!;
        public int RenderingBy { get; set; } = -1;
        public DateTime RenderingStartedAt { get; set; }
        public DateTime RenderingLastUpdate { get; set; }
        public double ProgressPercent { get; set; } = 0; // 0.00 ... 1.00
        public bool IsComplete { get; set; } = false;
        public bool IsSuccess { get; set; } = false;
        public string FailureReason { get; set; } = "";
        public DanserConfiguration RenderSettings { get; set; } = new();
    }
}
