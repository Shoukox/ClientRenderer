using ClientRenderer.Abstractions;
using ClientRenderer.Logging;
using ClientRenderer.Models;
using System.Text.Json;

namespace ClientRenderer.Startup;

public sealed class ConfigurationLoader : IConfigurationLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<AppConfiguration> LoadAsync()
    {
        string settingsDirectory = Path.Combine(AppContext.BaseDirectory, "settings");
        Directory.CreateDirectory(settingsDirectory);

        string cookieFile = Path.Combine(settingsDirectory, "cookie.txt");
        if (!File.Exists(cookieFile))
        {
            await File.WriteAllTextAsync(cookieFile, "INSERT YOUR OSU-SESSION COOKIE HERE");
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

    private static async Task ValidateOsuSessionCookie(string osuSessionCookie)
    {
        if (string.IsNullOrWhiteSpace(osuSessionCookie) || osuSessionCookie.Contains("INSERT YOUR OSU-SESSION COOKIE HERE", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("osu_session cookie is empty or placeholder.");
        }

        Logger.Log("Checking your osu_session cookie...");
        using var httpClient = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Head, "https://osu.ppy.sh/beatmapsets/41823/download");
        request.Headers.Add("Cookie", $"osu_session={osuSessionCookie}");
        request.Headers.Referrer = new Uri("https://osu.ppy.sh/beatmapsets/41823/download");
        var response = await httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("Invalid/expired osu_session cookie. Re-login on osu website and update settings/cookie.txt.");
        }

        Logger.Log("Your osu_session cookie is OK.");
    }

    private static async Task<T> ReadOrCreateJson<T>(string path, T defaultModel, string setupMessage) where T : class
    {
        if (!File.Exists(path))
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(defaultModel, JsonOptions));
            throw new InvalidOperationException($"{setupMessage} at {path}");
        }

        var json = await File.ReadAllTextAsync(path);
        var model = JsonSerializer.Deserialize<T>(json);
        return model ?? throw new InvalidOperationException($"Failed to parse config file: {path}");
    }
}
