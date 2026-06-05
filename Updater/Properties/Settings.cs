using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Updater.Utils;
using UpdaterLib;

namespace Updater.Properties
{
    /// <summary>
    /// Persistent updater settings backed by a single JSON file at a non-version-scoped
    /// path (e.g. <c>~/.local/share/Updater/settings.json</c>).
    ///
    /// We intentionally do NOT use <c>System.Configuration.ApplicationSettingsBase</c>
    /// here, because it stores <c>user.config</c> in a version-scoped subfolder
    /// (e.g. <c>.../Updater_Url_xxx/1.0.0.0/user.config</c>). When the updater is
    /// replaced with a new build that has a different assembly version, the runtime
    /// creates a new versioned folder and the settings appear blank.
    /// </summary>
    public sealed class Settings
    {
        private const string SettingsDirectoryName = "Updater";
        private const string SettingsFileName = "settings.json";

        private static readonly Lazy<Settings> defaultInstance = new(Load);

        public static Settings Default => defaultInstance.Value;

        public string ClientAppPath { get; set; } = "";
        public string UpdateServer { get; set; } = "";
        public string AppName { get; set; } = "";
        public UpdateInfo? LastVersion { get; set; }
        public bool AutoReboot { get; set; } = false;
        public bool ProgressFullscreen { get; set; } = true;
        public bool EnablePreReleaseVersions { get; set; } = false;

        /// <returns><c>true</c> if settings were written; <c>false</c> on IO/permission errors.</returns>
        public bool Save()
        {
            try
            {
                var path = GetSettingsFilePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(this, SerializerOptions));
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError("Failed to save updater settings.", ex);
                return false;
            }
        }

        public void Reload()
        {
            var fresh = Load();
            ClientAppPath = fresh.ClientAppPath;
            UpdateServer = fresh.UpdateServer;
            AppName = fresh.AppName;
            LastVersion = fresh.LastVersion;
            AutoReboot = fresh.AutoReboot;
            ProgressFullscreen = fresh.ProgressFullscreen;
            EnablePreReleaseVersions = fresh.EnablePreReleaseVersions;
        }

        private static Settings Load()
        {
            try
            {
                var path = GetSettingsFilePath();
                if (!File.Exists(path))
                {
                    return new Settings();
                }

                var content = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<Settings>(content, SerializerOptions);
                return loaded ?? new Settings();
            }
            catch (Exception ex)
            {
                Logger.LogError("Failed to load updater settings.", ex);
                return new Settings();
            }
        }

        private static string GetSettingsFilePath()
        {
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(baseDir))
            {
                baseDir = AppDomain.CurrentDomain.BaseDirectory;
            }

            return Path.Combine(baseDir, SettingsDirectoryName, SettingsFileName);
        }

        private static JsonSerializerOptions SerializerOptions => new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };
    }
}
