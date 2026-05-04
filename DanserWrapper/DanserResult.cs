namespace DanserWrapper
{
    public record DanserResult
    {
        public int ExitCode { get; set; }
        public string Output { get; set; } = string.Empty;
        public string Error { get; set; } = string.Empty;
        public bool Success { get; set; }
    }
}
