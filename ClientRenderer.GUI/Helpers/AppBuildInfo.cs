using System.Linq;
using System.Reflection;

namespace ClientRenderer.GUI.Helpers
{
    public static class AppBuildInfo
    {
        private static readonly Assembly Assembly = typeof(AppBuildInfo).Assembly;

        public static string Version => Assembly.GetName().Version?.ToString() ?? "unknown";

        public static string ReleaseDate =>
            Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(x => x.Key == "ReleaseDate")
                ?.Value
            ?? "unknown";

        public static string DisplayText => $"v{Version} - released {ReleaseDate}";
    }
}
