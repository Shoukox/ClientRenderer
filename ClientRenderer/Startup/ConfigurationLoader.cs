using ClientRenderer.Abstractions;
using ClientRenderer.Logging;
using ClientRenderer.Models;
using System.Text.Json;
using Velopack.Locators;

namespace ClientRenderer.Startup;

public sealed class ConfigurationLoader : IConfigurationLoader
{
    private const string SettingsDirectoryName = "settings";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<AppConfiguration> LoadAsync()
    {
        string legacySettingsDirectory = Path.Combine(AppContext.BaseDirectory, SettingsDirectoryName);
        string settingsDirectory = GetSettingsDirectory();
        Directory.CreateDirectory(settingsDirectory);
        Logger.Log($"Using renderer settings directory: {settingsDirectory}");
        MigrateLegacySettingsDirectory(legacySettingsDirectory, settingsDirectory);

        string cookieFile = Path.Combine(settingsDirectory, "cookie.txt");
        if (!File.Exists(cookieFile))
        {
            await File.WriteAllTextAsync(cookieFile, "INSERT YOUR OSU-SESSION COOKIE HERE");
            Logger.LogWarning($"Created missing osu_session cookie file: {cookieFile}");
            throw new InvalidOperationException($"Specify your osu_session cookie at {cookieFile}");
        }

        string osuSessionCookie = (await File.ReadAllTextAsync(cookieFile)).Trim();
        await ValidateOsuSessionCookie(osuSessionCookie);

        string osuApiConfigFilePath = Path.Combine(settingsDirectory, "osu-api.json");
        var osuApiConfig = await ReadOrCreateJson(
            osuApiConfigFilePath,
            new OsuApiV2Configuration(),
            "Specify your osu api v2 credentials");

        string rendererSettingsFilePath = Path.Combine(settingsDirectory, "renderer-settings.json");
        var rendererCredentials = await ReadOrCreateJson(
            rendererSettingsFilePath,
            new RendererCredentials(),
            "Specify your renderer settings. If you don't have it, contact Shoukko");

        return new AppConfiguration
        {
            SettingsDirectory = settingsDirectory,
            OsuSessionCookie = osuSessionCookie,
            OsuApiV2Configuration = osuApiConfig,
            RendererCredentials = rendererCredentials
        };
    }

    private static string GetSettingsDirectory()
    {
        if (VelopackLocator.IsCurrentSet && !VelopackLocator.Current.IsPortable && VelopackLocator.Current.RootAppDir is { } rootAppDirectory)
            return Path.Combine(rootAppDirectory, SettingsDirectoryName);

        return Path.Combine(AppContext.BaseDirectory, SettingsDirectoryName);
    }

    private static void MigrateLegacySettingsDirectory(string legacySettingsDirectory, string settingsDirectory)
    {
        if (Path.GetFullPath(legacySettingsDirectory).Equals(Path.GetFullPath(settingsDirectory), StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(legacySettingsDirectory))
        {
            return;
        }

        foreach (string sourceFile in Directory.EnumerateFiles(legacySettingsDirectory, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(legacySettingsDirectory, sourceFile);
            string destinationFile = Path.Combine(settingsDirectory, relativePath);

            if (File.Exists(destinationFile))
                continue;

            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            File.Copy(sourceFile, destinationFile);
            Logger.Log($"Migrated legacy settings file to: {destinationFile}");
        }
    }

    private static async Task ValidateOsuSessionCookie(string osuSessionCookie)
    {
        if (string.IsNullOrWhiteSpace(osuSessionCookie) || osuSessionCookie.Contains("INSERT YOUR OSU-SESSION COOKIE HERE", StringComparison.OrdinalIgnoreCase))
        {
            Logger.LogError("osu_session cookie is empty or still contains the placeholder value.");
            throw new InvalidOperationException("osu_session cookie is empty or placeholder.");
        }

        Logger.Log("Checking your osu_session cookie...");
        using HttpClient httpClient = new HttpClient();
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Head, "https://osu.ppy.sh/beatmapsets/41823/download");
        request.Headers.Add("Cookie", $"osu_session={osuSessionCookie}");
        request.Headers.Referrer = new Uri("https://osu.ppy.sh/beatmapsets/41823/download");
        var response = await httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            Logger.LogError($"osu_session validation failed with {(int)response.StatusCode} {response.StatusCode}.");
            throw new InvalidOperationException("Invalid/expired osu_session cookie. Re-login on osu website and update settings/cookie.txt.");
        }

        Logger.Log("Your osu_session cookie is OK.");
    }

    private static async Task<T> ReadOrCreateJson<T>(string path, T defaultModel, string setupMessage) where T : class
    {
        if (!File.Exists(path))
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(defaultModel, JsonOptions));
            Logger.LogWarning($"Created missing configuration file: {path}");
            throw new InvalidOperationException($"{setupMessage} at {path}");
        }

        var json = await File.ReadAllTextAsync(path);
        var model = JsonSerializer.Deserialize<T>(json);
        if (model is null)
        {
            Logger.LogError($"Failed to parse configuration file: {path}");
            throw new InvalidOperationException($"Failed to parse config file: {path}");
        }

        Logger.Log($"Loaded configuration file: {path}");
        return model;
    }
}
