using System.Reflection;

namespace ClientRenderer.Startup;

public static class ClientRendererVersion
{
    private const string UnknownVersion = "0.0.0";

    public static string Current { get; } = ResolveCurrentVersion();

    private static string ResolveCurrentVersion()
    {
        Assembly assembly = Assembly.GetEntryAssembly() ?? typeof(ClientRendererVersion).Assembly;
        string? informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            // Informational versions may contain build metadata (for example,
            // a source-control hash), which is not part of the release version.
            int metadataSeparator = informationalVersion.IndexOf('+');
            return (metadataSeparator >= 0
                    ? informationalVersion[..metadataSeparator]
                    : informationalVersion).TrimStart('v', 'V');
        }

        return assembly.GetName().Version?.ToString(3) ?? UnknownVersion;
    }
}
