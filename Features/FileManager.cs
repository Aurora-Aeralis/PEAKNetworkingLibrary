using System.IO;
using BepInEx.Configuration;
using BepInEx;

namespace NetworkingLibrary.Features
{
    public static class FileManager
    {
        const string VersionSection = "Version";
        const string VersionKey = "ConfigSchemaVersion";
        const string LegacyVersionKey = "Current Version";
        const string PluginVersionKey = "PluginVersion";
        const string CurrentConfigSchemaVersion = "1";

        internal static ConfigEntry<T> BindConfig<T>(string Header, string Features, T Value, string? Info = "")
        {
            return Net.Instance.config.Bind(Header, Features, Value, Info);
        }

        internal static void InitializeConfig()
        {
            string ConfigFolderPath = Path.Combine(Paths.ConfigPath, $"DAa Mods/{MyPluginInfo.PLUGIN_NAME}");
            if (!Directory.Exists(ConfigFolderPath)) Directory.CreateDirectory(ConfigFolderPath);
            Net.Instance.config = new ConfigFile(Path.Combine(ConfigFolderPath, "config.cfg"), true);
            MigrateConfigIfNeeded(Net.Instance.config, CurrentConfigSchemaVersion);

            DefineConfig();
            Net.Logger.LogInfo("Config initialized.");
        }

        internal static void DefineConfig()
        {
            BindConfig(VersionSection, VersionKey, CurrentConfigSchemaVersion, "Tracks config schema version for non-destructive migrations.");
            BindConfig(VersionSection, PluginVersionKey, MyPluginInfo.PLUGIN_VERSION, "Tracks plugin release version.");
        }

        internal static void MigrateConfigIfNeeded(ConfigFile config, string currentVersion)
        {
            var storedVersion = GetStoredVersion(config);
            if (storedVersion == currentVersion)
                return;

            MigrateConfig(config, storedVersion, currentVersion);
            config.Bind(VersionSection, VersionKey, currentVersion, "Tracks config schema version for non-destructive migrations.").Value = currentVersion;
            config.Save();
        }

        static string GetStoredVersion(ConfigFile config)
        {
            var version = config.Bind(VersionSection, VersionKey, string.Empty, "Tracks config schema version for non-destructive migrations.").Value;
            if (!string.IsNullOrWhiteSpace(version))
                return version;
            return config.Bind(VersionSection, LegacyVersionKey, string.Empty, string.Empty).Value;
        }

        static void MigrateConfig(ConfigFile config, string previousVersion, string currentVersion)
        {
            _ = currentVersion;
            if (string.IsNullOrWhiteSpace(previousVersion))
                return;

            var legacyVersion = config.Bind(VersionSection, LegacyVersionKey, string.Empty, string.Empty).Value;
            if (string.IsNullOrWhiteSpace(config.Bind(VersionSection, VersionKey, string.Empty, string.Empty).Value) && !string.IsNullOrWhiteSpace(legacyVersion))
                config.Bind(VersionSection, VersionKey, legacyVersion, "Tracks config schema version for non-destructive migrations.").Value = legacyVersion;
        }
    }
}
