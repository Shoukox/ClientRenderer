using ClientRenderer.Logging;
using ClientRenderer.Startup;
using System;
using System.IO;
using System.Text.Json;

namespace ClientRenderer.GUI.Configuration
{
    public sealed class AppSettingsProvider(string directoryPath)
    {
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        private readonly string _directoryPath = directoryPath ?? throw new ArgumentNullException(nameof(directoryPath));

        public const string ConfigFileName = "settings.json";
        public readonly string RendererSettingsDirectory = AppStoragePaths.GetSettingsDirectory();

        public string FilePath => Path.Combine(_directoryPath, ConfigFileName);

        public Settings Current { get; private set; } = new();

        public Settings Load()
        {
            Directory.CreateDirectory(_directoryPath);

            if (!File.Exists(FilePath))
            {
                Logger.Log($"Settings file was not found. Creating default settings at: {FilePath}");
                Current = new Settings();
                Save();
                return Current;
            }

            try
            {
                var json = File.ReadAllText(FilePath);
                Current = JsonSerializer.Deserialize<Settings>(json, SerializerOptions) ?? new Settings();
            }
            catch (JsonException ex)
            {
                Logger.LogError(ex, $"Settings file is invalid. Backing it up and recreating defaults: {FilePath}");
                BackupCorruptedFile();
                Current = new Settings();
                Save();
            }

            return Current;
        }

        public void Save()
        {
            Directory.CreateDirectory(_directoryPath);

            var tempFilePath = FilePath + ".tmp";
            var json = JsonSerializer.Serialize(Current, SerializerOptions);

            File.WriteAllText(tempFilePath, json);

            if (File.Exists(FilePath))
            {
                File.Delete(FilePath);
            }

            File.Move(tempFilePath, FilePath);
            Logger.Log($"Settings saved to: {FilePath}");
        }

        public void Update(Action<Settings> updateAction)
        {
            ArgumentNullException.ThrowIfNull(updateAction);

            updateAction(Current);
            Save();
        }

        private void BackupCorruptedFile()
        {
            var backupFilePath = Path.Combine(
                _directoryPath,
                $"settings.corrupted.{DateTime.UtcNow:yyyyMMddHHmmss}.json");

            File.Copy(FilePath, backupFilePath, overwrite: false);
            Logger.LogWarning($"Corrupted settings file was backed up to: {backupFilePath}");
        }
    }
}
