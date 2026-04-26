using System;
using System.IO;
using System.Collections;
using System.Reflection;
using System.Collections.Concurrent;
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
        static readonly ConcurrentDictionary<Type, Lazy<MethodInfo?>> RemoveMethodCache = new();
        static readonly ConcurrentDictionary<Type, Lazy<PropertyInfo?>> OrphanedEntriesPropertyCache = new();

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
            var legacyVersion = ReadLegacyVersion(config);
            var storedVersion = GetStoredVersion(schemaVersionEntry.Value, legacyVersion);
            var needsNormalization = string.IsNullOrWhiteSpace(schemaVersionEntry.Value) && !string.IsNullOrWhiteSpace(legacyVersion);
            if (storedVersion == currentVersion && !needsNormalization)
                return;

            MigrateConfig(schemaVersionEntry, storedVersion, currentVersion, legacyVersion);
            schemaVersionEntry.Value = currentVersion;
            DropLegacyVersionFromConfig(config);
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

        static string ReadLegacyVersion(ConfigFile config)
        {
            var configPath = config.ConfigFilePath;
            if (!File.Exists(configPath))
                return string.Empty;

            var inVersionSection = false;
            foreach (var line in File.ReadLines(configPath))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal))
                {
                    var section = trimmed[1..^1].Trim();
                    inVersionSection = section.Equals(VersionSection, StringComparison.Ordinal);
                    continue;
                }

                if (!inVersionSection)
                    continue;

                if (line.StartsWith($"{LegacyVersionKey} = ", StringComparison.Ordinal))
                    return line[(LegacyVersionKey.Length + 3)..].Trim();
            }

            return string.Empty;
        }

        static void DropLegacyVersionFromConfig(ConfigFile config)
        {
            var legacyDefinition = new ConfigDefinition(VersionSection, LegacyVersionKey);
            Exception? removeException = null;
            var removeMethod = GetRemoveMethod(config);
            var orphanedEntriesProperty = GetOrphanedEntriesProperty(config);
            if (removeMethod == null && orphanedEntriesProperty == null)
            {
                ClearLegacyVersion(config);
                return;
            }

            try
            {
                if (removeMethod != null)
                {
                    removeMethod.Invoke(config, new object[] { legacyDefinition });
                    return;
                }
            }
            catch (Exception exception)
            {
                removeException = exception;
            }

            try
            {
                var orphanedEntries = GetOrphanedEntries(config, orphanedEntriesProperty);
                orphanedEntries?.Remove(legacyDefinition);
            }
            catch (Exception orphanedEntriesException)
            {
                var removeContext = removeException == null ? string.Empty : $" Remove invocation error: {removeException}.";
                Net.Logger?.LogWarning($"Failed to remove legacy config key '{LegacyVersionKey}' during migration.{removeContext} OrphanedEntries fallback error: {orphanedEntriesException}");
            }
        }

        static void ClearLegacyVersion(ConfigFile config)
        {
            var configPath = config.ConfigFilePath;
            if (!File.Exists(configPath))
                return;

            var lines = File.ReadAllLines(configPath);
            var changed = false;
            var inVersionSection = false;
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal))
                {
                    var section = trimmed[1..^1].Trim();
                    inVersionSection = section.Equals(VersionSection, StringComparison.Ordinal);
                    continue;
                }

                if (!inVersionSection)
                    continue;

                if (!lines[i].StartsWith($"{LegacyVersionKey} = ", StringComparison.Ordinal))
                    continue;

                lines[i] = $"{LegacyVersionKey} =";
                changed = true;
            }

            if (changed)
                File.WriteAllLines(configPath, lines);
        }

        internal static MethodInfo? GetRemoveMethod(ConfigFile config)
        {
            var configType = config.GetType();
            var removeMethod = RemoveMethodCache.GetOrAdd(
                configType,
                type => new Lazy<MethodInfo?>(() => type.GetMethod("Remove", new[] { typeof(ConfigDefinition) })));
            return removeMethod.Value;
        }

        internal static PropertyInfo? GetOrphanedEntriesProperty(ConfigFile config)
        {
            var configType = config.GetType();
            var orphanedEntriesProperty = OrphanedEntriesPropertyCache.GetOrAdd(
                configType,
                type => new Lazy<PropertyInfo?>(() => type.GetProperty("OrphanedEntries", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)));
            return orphanedEntriesProperty.Value;
        }

        internal static IDictionary? GetOrphanedEntries(ConfigFile config, PropertyInfo? orphanedEntriesProperty = null)
        {
            orphanedEntriesProperty ??= GetOrphanedEntriesProperty(config);
            return orphanedEntriesProperty?.GetValue(config) as IDictionary;
        }

        static ConfigEntry<string> BindPluginVersion(ConfigFile config, string value)
        {
            return config.Bind(VersionSection, PluginVersionKey, value, PluginVersionInfo);
        }
    }
}
