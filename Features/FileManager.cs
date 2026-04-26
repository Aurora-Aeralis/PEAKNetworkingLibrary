using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx.Configuration;
using BepInEx;
using UnityEngine;

namespace NetworkingLibrary.Features
{
    public static class FileManager
    {
        const string VersionSection = "Version";
        const string VersionKey = "ConfigSchemaVersion";
        const string LegacyVersionKey = "Current Version";
        const string PluginVersionKey = "PluginVersion";
        const string CurrentConfigSchemaVersion = "1";
        const string SchemaVersionInfo = "Tracks config schema version for non-destructive migrations.";
        const string PluginVersionInfo = "Tracks plugin release version.";

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
            BindSchemaVersion(Net.Instance.config, CurrentConfigSchemaVersion);
            BindPluginVersion(Net.Instance.config, MyPluginInfo.PLUGIN_VERSION);
        }

        internal static void MigrateConfigIfNeeded(ConfigFile config, string currentVersion)
        {
            var schemaVersionEntry = BindSchemaVersion(config, string.Empty);
            var legacyVersionEntry = BindLegacyVersion(config);
            var storedVersion = GetStoredVersion(schemaVersionEntry, legacyVersionEntry);
            if (storedVersion == currentVersion)
                return;

            MigrateConfig(schemaVersionEntry, legacyVersionEntry, storedVersion, currentVersion);
            schemaVersionEntry.Value = currentVersion;
            config.Save();
        }

        static string GetStoredVersion(ConfigEntry<string> schemaVersionEntry, ConfigEntry<string> legacyVersionEntry)
        {
            var version = schemaVersionEntry.Value;
            if (!string.IsNullOrWhiteSpace(version))
                return version;
            return legacyVersionEntry.Value;
        }

        static void MigrateConfig(ConfigEntry<string> schemaVersionEntry, ConfigEntry<string> legacyVersionEntry, string previousVersion, string currentVersion)
        {
            _ = currentVersion;
            if (string.IsNullOrWhiteSpace(previousVersion))
                return;

            var legacyVersion = legacyVersionEntry.Value;
            if (string.IsNullOrWhiteSpace(schemaVersionEntry.Value) && !string.IsNullOrWhiteSpace(legacyVersion))
                schemaVersionEntry.Value = legacyVersion;
        }

        static ConfigEntry<string> BindSchemaVersion(ConfigFile config, string value)
        {
            return config.Bind(VersionSection, VersionKey, value, SchemaVersionInfo);
        }

        static ConfigEntry<string> BindLegacyVersion(ConfigFile config)
        {
            return config.Bind(VersionSection, LegacyVersionKey, string.Empty, string.Empty);
        }

        static ConfigEntry<string> BindPluginVersion(ConfigFile config, string value)
        {
            return config.Bind(VersionSection, PluginVersionKey, value, PluginVersionInfo);
        }
    }
}
