using System;
using System.IO;
using System.Collections.Generic;
using System.Reflection;
using System.Collections.Concurrent;
using System.Globalization;
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
        static readonly object ConfigMigrationLock = new();
        static readonly ConcurrentDictionary<Type, Lazy<MethodInfo?>> RemoveMethodCache = new();
        static readonly ConcurrentDictionary<Type, Lazy<PropertyInfo?>> OrphanedEntriesPropertyCache = new();

        internal static ConfigEntry<T> BindConfig<T>(string header, string features, T value, string? info = "")
        {
            return Net.Instance.config.Bind(header, features, value, info);
        }

        internal static void InitializeConfig()
        {
            var configFolderPath = Path.Combine(Paths.ConfigPath, DaModsFolderName, MyPluginInfo.PLUGIN_NAME);
            if (!Directory.Exists(configFolderPath)) Directory.CreateDirectory(configFolderPath);
            Net.Instance.config = new ConfigFile(BuildConfigPath(configFolderPath), true);
            MigrateConfigIfNeeded(Net.Instance.config, CurrentConfigSchemaVersion);

            DefineConfig();
            Net.Logger.LogInfo("Config initialization complete.");
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
            lock (ConfigMigrationLock)
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
            var sourceVersionValid = TryParseSchemaVersion(sourceVersion, out var startVersion);
            if (!sourceVersionValid || startVersion < 0)
            {
                startVersion = 0;
                Net.Logger?.LogWarning($"Invalid source schema version '{sourceVersion}'. Starting migration at schema 0.");
            }

            for (var fromSchemaVersion = startVersion; fromSchemaVersion < targetVersion; fromSchemaVersion++)
            {
                switch (fromSchemaVersion)
                {
                    case 0:
                        Migrate_0_to_1(schemaVersionEntry, legacyVersion);
                        break;
                    default:
                        throw new InvalidOperationException($"No migration path exists from schema {fromSchemaVersion} to {fromSchemaVersion + 1}.");
                }
            }
        }

        static bool TryParseSchemaVersion(string version, out int schemaVersion)
        {
            schemaVersion = 0;
            if (string.IsNullOrWhiteSpace(version) || !int.TryParse(version, NumberStyles.Integer, CultureInfo.InvariantCulture, out schemaVersion))
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

                return NormalizeLegacyVersionValue(line[(separatorIndex + 1)..]);
            }

            return string.Empty;
        }

        static string NormalizeLegacyVersionValue(string value)
        {
            var normalized = value;
            var semicolonIndex = normalized.IndexOf(';');
            var hashIndex = normalized.IndexOf('#');
            var commentIndex = semicolonIndex < 0 ? hashIndex : hashIndex < 0 ? semicolonIndex : Math.Min(semicolonIndex, hashIndex);
            if (commentIndex >= 0)
                normalized = normalized[..commentIndex];
            return normalized.Trim();
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

            Net.Logger?.LogWarning($"Legacy config key '{LegacyVersionKey}' could not be removed during migration.{BuildCleanupFailureContext(removeException, orphanedEntriesException, fileCleanupException)}");
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
            var stage = "initialize";
            var configPath = config.ConfigFilePath;
            try
            {
                if (!File.Exists(configPath))
                    return false;

                stage = "read";
                var lines = File.ReadAllLines(configPath);
                var keptLines = new List<string>(lines.Length);
                var changed = false;
                var inVersionSection = false;
                stage = "normalize";
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

                stage = "write-temp";
                var directoryPath = Path.GetDirectoryName(configPath);
                if (string.IsNullOrWhiteSpace(directoryPath))
                    throw new InvalidOperationException("Config path does not contain a directory.");

                var tempPath = Path.Combine(directoryPath, $"{Path.GetFileName(configPath)}.{Guid.NewGuid():N}.tmp");
                try
                {
                    File.WriteAllLines(tempPath, keptLines);
                    stage = "replace";
                    if (File.Exists(configPath))
                    {
                        try
                        {
                            File.Replace(tempPath, configPath, null);
                        }
                        catch (PlatformNotSupportedException)
                        {
                            File.Move(tempPath, configPath, true);
                        }
                    }
                    else
                    {
                        File.Move(tempPath, configPath, true);
                    }
                }
                finally
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }

                return true;
            }
            catch (Exception ex)
            {
                exception = new InvalidOperationException($"Failed to clear legacy version in config file at '{configPath}' during stage '{stage}'.", ex);
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
