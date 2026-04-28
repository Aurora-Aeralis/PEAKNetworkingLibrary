using System;
using System.IO;
using System.Collections.Generic;
using System.Reflection;
using System.Collections.Concurrent;
using System.Globalization;
using System.Threading;
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
        const int ConfigSaveRetryCount = 4;
        const int ConfigIoRetryDelayMilliseconds = 15;
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
                var saveOnConfigSet = config.SaveOnConfigSet;
                config.SaveOnConfigSet = false;
                try
                {
                    var schemaVersionEntry = BindSchemaVersion(config, string.Empty);
                    var hasLegacyVersion = TryReadLegacyVersion(config, out var legacyVersion);
                    var legacyDefinition = new ConfigDefinition(VersionSection, LegacyVersionKey);
                    var hasLegacyInConfig = hasLegacyVersion || HasLegacyVersionInConfig(config, legacyDefinition);
                    var storedVersion = GetStoredVersion(schemaVersionEntry.Value, legacyVersion);
                    if (TryParseSchemaVersion(storedVersion, out var storedSchemaVersion)
                        && TryParseSchemaVersion(currentVersion, out var currentSchemaVersion)
                        && storedSchemaVersion > currentSchemaVersion)
                    {
                        Net.Logger?.LogWarning($"Stored config schema version '{storedVersion}' is newer than supported schema '{currentVersion}'. Migration skipped to avoid destructive downgrade.");
                        return;
                    }

                    var needsNormalization = string.IsNullOrWhiteSpace(schemaVersionEntry.Value) && hasLegacyVersion;
                    if (storedVersion == currentVersion && !needsNormalization && !hasLegacyInConfig)
                        return;

                    bool shouldPersistTargetVersion;
                    if (string.IsNullOrWhiteSpace(storedVersion))
                    {
                        if (!TryParseSchemaVersion(currentVersion, out _))
                            throw new InvalidOperationException($"Current config schema version '{currentVersion}' is not a valid schema identifier.");
                        shouldPersistTargetVersion = true;
                    }
                    else
                    {
                        shouldPersistTargetVersion = MigrateConfig(schemaVersionEntry, storedVersion, currentVersion, legacyVersion);
                    }
                    if (!shouldPersistTargetVersion)
                        return;

                    schemaVersionEntry.Value = currentVersion;
                    Exception? removeException = null, orphanedEntriesException = null;
                    var cleanedInMemory = !hasLegacyInConfig || DropLegacyVersionFromConfig(config, legacyDefinition, out removeException, out orphanedEntriesException);
                    SaveConfig(config);
                    Exception? fileCleanupException = null;
                    var cleanedFile = !hasLegacyVersion && cleanedInMemory || ClearLegacyVersionInFile(config, out fileCleanupException);
                    if (!cleanedInMemory && !cleanedFile)
                        Net.Logger?.LogWarning($"Legacy config key '{LegacyVersionKey}' could not be removed during migration.{BuildCleanupFailureContext(removeException, orphanedEntriesException, fileCleanupException)}");
                }
                finally
                {
                    config.SaveOnConfigSet = saveOnConfigSet;
                }
            }
        }

        static string GetStoredVersion(string schemaVersion, string legacyVersion)
        {
            if (!string.IsNullOrWhiteSpace(schemaVersion))
                return schemaVersion;
            return legacyVersion;
        }

        static bool MigrateConfig(ConfigEntry<string> schemaVersionEntry, string previousVersion, string currentVersion, string legacyVersion)
        {
            if (!TryParseSchemaVersion(currentVersion, out var targetVersion))
                throw new InvalidOperationException($"Current config schema version '{currentVersion}' is not a valid schema identifier.");

            var sourceVersion = string.IsNullOrWhiteSpace(schemaVersionEntry.Value) ? legacyVersion : previousVersion;
            var sourceVersionValid = TryParseSchemaVersion(sourceVersion, out var startVersion);
            if (!sourceVersionValid || startVersion < 0)
            {
                startVersion = 0;
                if (!string.IsNullOrWhiteSpace(sourceVersion))
                    Net.Logger?.LogWarning($"Invalid source schema version '{sourceVersion}'. Starting migration at schema 0.");
            }
            else if (startVersion > targetVersion)
            {
                Net.Logger?.LogWarning($"Config schema version downgrade detected ({startVersion} -> {targetVersion}). Skipping downgrade migrations and retaining existing schema data.");
                return false;
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

            return true;
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

        static bool TryReadLegacyVersion(ConfigFile config, out string legacyVersion)
        {
            legacyVersion = string.Empty;
            var configPath = config.ConfigFilePath;
            if (!File.Exists(configPath))
                return false;

            var inVersionSection = false;
            foreach (var line in ReadAllLinesWithRetry(configPath))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal))
                {
                    var section = trimmed[1..^1].Trim();
                    inVersionSection = section.Equals(VersionSection, StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inVersionSection)
                    continue;

                if (string.IsNullOrWhiteSpace(trimmed) || IsConfigComment(trimmed))
                    continue;

                var separatorIndex = line.IndexOf('=');
                if (separatorIndex <= 0)
                    continue;

                var key = line[..separatorIndex].Trim();
                if (!key.Equals(LegacyVersionKey, StringComparison.OrdinalIgnoreCase))
                    continue;

                legacyVersion = NormalizeLegacyVersionValue(line[(separatorIndex + 1)..]);
                return true;
            }

            return false;
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

        static bool IsConfigComment(string trimmed)
        {
            return trimmed.StartsWith("#", StringComparison.Ordinal) || trimmed.StartsWith(";", StringComparison.Ordinal);
        }

        static bool DropLegacyVersionFromConfig(ConfigFile config, ConfigDefinition legacyDefinition, out Exception? removeException, out Exception? orphanedEntriesException)
        {
            if (TryRemoveViaConfigApi(config, legacyDefinition, out removeException))
            {
                orphanedEntriesException = null;
                return true;
            }
            return TryRemoveViaOrphanedEntries(config, legacyDefinition, out orphanedEntriesException);
        }

        static bool HasLegacyVersionInConfig(ConfigFile config, ConfigDefinition legacyDefinition)
        {
            try
            {
                if (config.ContainsKey(legacyDefinition))
                    return true;
                return GetOrphanedEntries(config)?.Contains(legacyDefinition) == true;
            }
            catch
            {
                return true;
            }
        }

        static void SaveConfig(ConfigFile config)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    config.Save();
                    return;
                }
                catch (IOException) when (attempt < ConfigSaveRetryCount)
                {
                    Thread.Sleep(ConfigIoRetryDelayMilliseconds * attempt);
                }
                catch (UnauthorizedAccessException) when (attempt < ConfigSaveRetryCount)
                {
                    Thread.Sleep(ConfigIoRetryDelayMilliseconds * attempt);
                }
            }
        }

        static string[] ReadAllLinesWithRetry(string path)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return File.ReadAllLines(path);
                }
                catch (IOException) when (attempt < ConfigSaveRetryCount)
                {
                    Thread.Sleep(ConfigIoRetryDelayMilliseconds * attempt);
                }
                catch (UnauthorizedAccessException) when (attempt < ConfigSaveRetryCount)
                {
                    Thread.Sleep(ConfigIoRetryDelayMilliseconds * attempt);
                }
            }
        }

        static bool TryRemoveViaConfigApi(ConfigFile config, ConfigDefinition legacyDefinition, out Exception? exception)
        {
            exception = null;
            var removeMethod = GetRemoveMethod(config);
            if (removeMethod == null)
                return false;

            try
            {
                var result = removeMethod.Invoke(config, new object[] { legacyDefinition });
                if (result is bool removed)
                    return removed;
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
                var lines = ReadAllLinesWithRetry(configPath);
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
                        inVersionSection = section.Equals(VersionSection, StringComparison.OrdinalIgnoreCase);
                        keptLines.Add(lines[i]);
                        continue;
                    }

                    if (!inVersionSection)
                    {
                        keptLines.Add(lines[i]);
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(trimmed) || IsConfigComment(trimmed))
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
                    if (!key.Equals(LegacyVersionKey, StringComparison.OrdinalIgnoreCase))
                    {
                        keptLines.Add(lines[i]);
                        continue;
                    }

                    DropTrailingLegacyMetadata(keptLines);
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
                        catch (Exception ex) when (ex is PlatformNotSupportedException || ex is IOException || ex is UnauthorizedAccessException)
                        {
                            File.Copy(tempPath, configPath, true);
                        }
                    }
                    else
                    {
                        File.Move(tempPath, configPath);
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

        static void DropTrailingLegacyMetadata(List<string> keptLines)
        {
            var index = keptLines.Count - 1;
            while (index >= 0 && string.IsNullOrWhiteSpace(keptLines[index])) index--;
            var commentEnd = index;
            while (index >= 0 && IsConfigComment(keptLines[index].Trim())) index--;
            if (commentEnd == index) return;
            keptLines.RemoveRange(index + 1, keptLines.Count - index - 1);
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

        internal static System.Collections.IDictionary? GetOrphanedEntries(ConfigFile config, PropertyInfo? orphanedEntriesProperty = null)
        {
            orphanedEntriesProperty ??= GetOrphanedEntriesProperty(config);
            return orphanedEntriesProperty?.GetValue(config) as System.Collections.IDictionary;
        }

        static ConfigEntry<string> BindPluginVersion(ConfigFile config, string value)
        {
            return config.Bind(VersionSection, PluginVersionKey, value, PluginVersionInfo);
        }
    }
}
