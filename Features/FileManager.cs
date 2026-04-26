using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
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
            if (!TryParseSchemaVersion(currentVersion, out var targetVersion))
                throw new InvalidOperationException($"Current config schema version '{currentVersion}' is not a valid schema identifier.");

            var schemaVersionEntry = BindSchemaVersion(config, string.Empty);
            var legacyVersion = ReadLegacyVersion(config);
            var storedVersion = GetStoredVersion(schemaVersionEntry.Value, legacyVersion);
            var sourceVersion = string.IsNullOrWhiteSpace(schemaVersionEntry.Value) ? legacyVersion : storedVersion;
            var sourceVersionValid = TryParseSchemaVersion(sourceVersion, out var startVersion);
            if (!sourceVersionValid || startVersion < 0)
            {
                startVersion = 0;
                Net.Logger?.LogWarning($"Invalid source config schema version '{sourceVersion}'. Defaulting migration start version to 0.");
            }

            var needsNormalization = string.IsNullOrWhiteSpace(schemaVersionEntry.Value) && !string.IsNullOrWhiteSpace(legacyVersion);
            if (startVersion == targetVersion && !needsNormalization)
                return;

            if (startVersion > targetVersion)
            {
                Net.Logger?.LogError($"Config schema downgrade is not supported. Stored schema version '{sourceVersion}' is newer than target schema version '{currentVersion}'.");
                return;
            }

            MigrateConfig(schemaVersionEntry, startVersion, targetVersion, legacyVersion);
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

        static void MigrateConfig(ConfigEntry<string> schemaVersionEntry, int sourceVersion, int targetVersion, string legacyVersion)
        {
            if (sourceVersion > targetVersion)
                throw new InvalidOperationException($"Config schema downgrade is not supported from {sourceVersion} to {targetVersion}.");

            for (var schemaVersion = sourceVersion; schemaVersion < targetVersion; schemaVersion++)
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
            if (string.IsNullOrWhiteSpace(version) || !int.TryParse(version, out schemaVersion))
                return false;
            return schemaVersion >= 0;
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

                if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#", StringComparison.Ordinal) || trimmed.StartsWith(";", StringComparison.Ordinal))
                    continue;

                var separatorIndex = line.IndexOf('=');
                if (separatorIndex <= 0)
                    continue;

                var key = line[..separatorIndex].Trim();
                if (!key.Equals(LegacyVersionKey, StringComparison.Ordinal))
                    continue;

                return line[(separatorIndex + 1)..].Trim();
            }

            return string.Empty;
        }

        static void DropLegacyVersionFromConfig(ConfigFile config)
        {
            var legacyDefinition = new ConfigDefinition(VersionSection, LegacyVersionKey);
            if (TryRemoveViaConfigApi(config, legacyDefinition, out var removeException))
                return;
            if (TryRemoveViaOrphanedEntries(config, legacyDefinition, out var orphanedEntriesException))
                return;
            if (ClearLegacyVersionInFile(config, out var fileCleanupException))
                return;

            Net.Logger?.LogWarning($"Failed to remove legacy config key '{LegacyVersionKey}' during migration.{BuildCleanupFailureContext(removeException, orphanedEntriesException, fileCleanupException)}");
        }

        static bool TryRemoveViaConfigApi(ConfigFile config, ConfigDefinition legacyDefinition, out Exception? exception)
        {
            exception = null;
            var removeMethod = GetRemoveMethod(config);
            if (removeMethod == null)
                return false;

            try
            {
                removeMethod.Invoke(config, new object[] { legacyDefinition });
                return true;
            }
            catch (Exception ex)
            {
                exception = ex;
                return false;
            }
        }

        static bool TryRemoveViaOrphanedEntries(ConfigFile config, ConfigDefinition legacyDefinition, out Exception? exception)
        {
            exception = null;
            var orphanedEntriesProperty = GetOrphanedEntriesProperty(config);
            if (orphanedEntriesProperty == null)
                return false;

            try
            {
                var orphanedEntries = GetOrphanedEntries(config, orphanedEntriesProperty);
                if (orphanedEntries == null)
                    return false;

                var existedBeforeRemoval = orphanedEntries.Contains(legacyDefinition);
                orphanedEntries.Remove(legacyDefinition);
                var removed = existedBeforeRemoval && !orphanedEntries.Contains(legacyDefinition);
                return removed;
            }
            catch (Exception ex)
            {
                exception = ex;
                return false;
            }
        }

        static bool ClearLegacyVersionInFile(ConfigFile config, out Exception? exception)
        {
            exception = null;
            try
            {
                var configPath = config.ConfigFilePath;
                if (!File.Exists(configPath))
                    return false;

                var lines = File.ReadAllLines(configPath);
                var keptLines = new List<string>(lines.Length);
                var changed = false;
                var inVersionSection = false;
                for (var i = 0; i < lines.Length; i++)
                {
                    var trimmed = lines[i].Trim();
                    if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal))
                    {
                        var section = trimmed[1..^1].Trim();
                        inVersionSection = section.Equals(VersionSection, StringComparison.Ordinal);
                        keptLines.Add(lines[i]);
                        continue;
                    }

                    if (!inVersionSection)
                    {
                        keptLines.Add(lines[i]);
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#", StringComparison.Ordinal) || trimmed.StartsWith(";", StringComparison.Ordinal))
                    {
                        keptLines.Add(lines[i]);
                        continue;
                    }

                    var separatorIndex = lines[i].IndexOf('=');
                    if (separatorIndex <= 0)
                    {
                        keptLines.Add(lines[i]);
                        continue;
                    }

                    var key = lines[i][..separatorIndex].Trim();
                    if (!key.Equals(LegacyVersionKey, StringComparison.Ordinal))
                    {
                        keptLines.Add(lines[i]);
                        continue;
                    }

                    changed = true;
                }

                if (!changed)
                    return false;

                File.WriteAllLines(configPath, keptLines);
                return true;
            }
            catch (Exception ex)
            {
                exception = ex;
                return false;
            }
        }

        static string BuildCleanupFailureContext(Exception? removeException, Exception? orphanedEntriesException, Exception? fileCleanupException)
        {
            var removeContext = removeException == null ? string.Empty : $" Remove API error: {removeException}.";
            var orphanedEntriesContext = orphanedEntriesException == null ? string.Empty : $" OrphanedEntries error: {orphanedEntriesException}.";
            var fileCleanupContext = fileCleanupException == null ? string.Empty : $" File cleanup error: {fileCleanupException}.";
            return $"{removeContext}{orphanedEntriesContext}{fileCleanupContext}";
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
