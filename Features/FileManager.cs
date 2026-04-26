using System;
using System.IO;
using System.Collections;
using System.Reflection;
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
        const string SchemaVersionInfo = "Tracks config schema version for non-destructive migrations.";
        const string PluginVersionInfo = "Tracks plugin release version.";
        const string DaModsFolderName = "DAa Mods";
        const string ConfigFileName = "config.cfg";

        internal static ConfigEntry<T> BindConfig<T>(string Header, string Features, T Value, string? Info = "")
        {
            return Net.Instance.config.Bind(Header, Features, Value, Info);
        }

        internal static void InitializeConfig()
        {
            string ConfigFolderPath = Path.Combine(Paths.ConfigPath, DaModsFolderName, MyPluginInfo.PLUGIN_NAME);
            if (!Directory.Exists(ConfigFolderPath)) Directory.CreateDirectory(ConfigFolderPath);
            Net.Instance.config = new ConfigFile(BuildConfigPath(ConfigFolderPath), true);
            MigrateConfigIfNeeded(Net.Instance.config, CurrentConfigSchemaVersion);

            DefineConfig();
            Net.Logger.LogInfo("Config initialized.");
        }

        internal static string BuildConfigPath(string configFolderPath)
        {
            return Path.Combine(configFolderPath, ConfigFileName);
        }

        internal static void DefineConfig()
        {
            BindSchemaVersion(Net.Instance.config, CurrentConfigSchemaVersion);
            BindPluginVersion(Net.Instance.config, MyPluginInfo.PLUGIN_VERSION);
        }

        internal static void MigrateConfigIfNeeded(ConfigFile config, string currentVersion)
        {
            var schemaVersionEntry = BindSchemaVersion(config, string.Empty);
            var legacyVersionEntry = BindLegacyVersion(config, string.Empty);
            var legacyVersion = legacyVersionEntry.Value;
            var hasLegacyVersionKey = HasLegacyVersionKey(config.ConfigFilePath) || !string.IsNullOrWhiteSpace(legacyVersion);
            var storedVersion = GetStoredVersion(schemaVersionEntry.Value, legacyVersion);
            var needsNormalization = string.IsNullOrWhiteSpace(schemaVersionEntry.Value) && !string.IsNullOrWhiteSpace(legacyVersion);
            var changed = false;

            if (storedVersion != currentVersion || needsNormalization)
            {
                MigrateConfig(schemaVersionEntry, storedVersion, currentVersion, legacyVersion);
                if (schemaVersionEntry.Value != currentVersion)
                {
                    schemaVersionEntry.Value = currentVersion;
                    changed = true;
                }
            }

            changed |= RemoveLegacyVersion(config, legacyVersionEntry, hasLegacyVersionKey);
            if (changed)
                config.Save();
        }

        static string GetStoredVersion(string schemaVersion, string legacyVersion)
        {
            if (!string.IsNullOrWhiteSpace(schemaVersion))
                return schemaVersion;
            return legacyVersion;
        }

        static void MigrateConfig(ConfigEntry<string> schemaVersionEntry, string previousVersion, string currentVersion, string legacyVersion)
        {
            if (!TryParseSchemaVersion(currentVersion, out var targetVersion))
                throw new InvalidOperationException($"Current config schema version '{currentVersion}' is not a valid schema identifier.");

            var sourceVersion = string.IsNullOrWhiteSpace(schemaVersionEntry.Value) ? legacyVersion : previousVersion;
            if (!TryParseSchemaVersion(sourceVersion, out var startVersion))
                startVersion = 0;

            for (var schemaVersion = startVersion; schemaVersion < targetVersion; schemaVersion++)
            {
                switch (schemaVersion)
                {
                    case 0:
                        Migrate_0_to_1(schemaVersionEntry, legacyVersion);
                        break;
                    default:
                        throw new InvalidOperationException($"No migration path exists from schema {schemaVersion} to {schemaVersion + 1}.");
                }
            }
        }

        static bool TryParseSchemaVersion(string version, out int schemaVersion)
        {
            schemaVersion = 0;
            return !string.IsNullOrWhiteSpace(version) && int.TryParse(version, out schemaVersion);
        }

        static void Migrate_0_to_1(ConfigEntry<string> schemaVersionEntry, string legacyVersion)
        {
            if (string.IsNullOrWhiteSpace(schemaVersionEntry.Value) && !string.IsNullOrWhiteSpace(legacyVersion))
                schemaVersionEntry.Value = legacyVersion;
        }

        static ConfigEntry<string> BindSchemaVersion(ConfigFile config, string value)
        {
            return config.Bind(VersionSection, VersionKey, value, SchemaVersionInfo);
        }

        static bool RemoveLegacyVersion(ConfigFile config, ConfigEntry<string> legacyVersionEntry, bool hasLegacyVersionKey)
        {
            if (!hasLegacyVersionKey && string.IsNullOrWhiteSpace(legacyVersionEntry.Value))
                return false;

            var legacyDefinition = new ConfigDefinition(VersionSection, LegacyVersionKey);
            Exception removeException = null;
            var changed = false;

            if (!string.IsNullOrWhiteSpace(legacyVersionEntry.Value))
            {
                legacyVersionEntry.Value = string.Empty;
                changed = true;
            }

            try
            {
                var removeMethod = GetRemoveMethod(config);
                if (removeMethod != null)
                {
                    removeMethod.Invoke(config, new object[] { legacyDefinition });
                    return true;
                }
            }
            catch (Exception exception)
            {
                removeException = exception;
            }

            try
            {
                var orphanedEntries = GetOrphanedEntries(config);
                if (orphanedEntries != null && orphanedEntries.Contains(legacyDefinition))
                {
                    orphanedEntries.Remove(legacyDefinition);
                    changed = true;
                }
            }
            catch (Exception orphanedEntriesException)
            {
                var removeContext = removeException == null ? string.Empty : $" Remove invocation error: {removeException}.";
                Net.Logger?.LogWarning($"Failed to remove legacy config key '{LegacyVersionKey}' during migration.{removeContext} OrphanedEntries fallback error: {orphanedEntriesException}");
            }

            return changed;
        }

        internal static MethodInfo GetRemoveMethod(ConfigFile config)
        {
            return config.GetType().GetMethod("Remove", new[] { typeof(ConfigDefinition) });
        }

        internal static IDictionary GetOrphanedEntries(ConfigFile config)
        {
            var orphanedEntriesProperty = config.GetType().GetProperty("OrphanedEntries", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return orphanedEntriesProperty?.GetValue(config) as IDictionary;
        }

        static ConfigEntry<string> BindPluginVersion(ConfigFile config, string value)
        {
            return config.Bind(VersionSection, PluginVersionKey, value, PluginVersionInfo);
        }

        static ConfigEntry<string> BindLegacyVersion(ConfigFile config, string value)
        {
            return config.Bind(VersionSection, LegacyVersionKey, value, SchemaVersionInfo);
        }

        static bool HasLegacyVersionKey(string configPath)
        {
            if (!File.Exists(configPath))
                return false;

            foreach (var line in File.ReadLines(configPath))
            {
                if (line.StartsWith($"{LegacyVersionKey} =", StringComparison.Ordinal))
                    return true;
            }

            return false;
        }
    }
}
