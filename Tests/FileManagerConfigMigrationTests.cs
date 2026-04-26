using BepInEx.Configuration;
using BepInEx.Logging;
using NetworkingLibrary.Features;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Xunit;

namespace NetworkingLibrary.Tests;

public class FileManagerConfigMigrationTests
{
    [Fact]
    public void MigrateConfigIfNeeded_EmptyConfig_SetsCurrentSchemaVersion()
    {
        using var scope = new TempConfigScope();
        var config = new ConfigFile(scope.ConfigPath, true);

        FileManager.MigrateConfigIfNeeded(config, "1");

        var schemaVersion = config.Bind("Version", "ConfigSchemaVersion", string.Empty).Value;
        var pluginVersion = config.Bind("Version", "PluginVersion", string.Empty).Value;
        var configText = File.ReadAllText(scope.ConfigPath);

        Assert.Equal("1", schemaVersion);
        Assert.DoesNotContain("Current Version =", configText, StringComparison.Ordinal);
        Assert.Contains("ConfigSchemaVersion = 1", configText, StringComparison.Ordinal);
        Assert.DoesNotContain("PluginVersion =", configText, StringComparison.Ordinal);
        Assert.True(string.IsNullOrWhiteSpace(pluginVersion));
    }

    [Fact]
    public void MigrateConfigIfNeeded_LegacyVersionOnly_NormalizesToSchemaVersionAndClearsLegacyAuthority()
    {
        using var scope = new TempConfigScope();
        var seedConfig = new ConfigFile(scope.ConfigPath, true);
        seedConfig.Bind("Version", "Current Version", string.Empty).Value = "1";
        seedConfig.Save();

        var config = new ConfigFile(scope.ConfigPath, true);
        FileManager.MigrateConfigIfNeeded(config, "1");

        var schemaVersion = config.Bind("Version", "ConfigSchemaVersion", string.Empty).Value;
        var configText = File.ReadAllText(scope.ConfigPath);

        Assert.Equal("1", schemaVersion);
        Assert.Contains("ConfigSchemaVersion = 1", configText, StringComparison.Ordinal);
        Assert.DoesNotContain("Current Version = 1", configText, StringComparison.Ordinal);

        config.Bind("Version", "ConfigSchemaVersion", string.Empty).Value = "1";
        config.Bind("Version", "Current Version", string.Empty).Value = "999";
        config.Save();

        var reloaded = new ConfigFile(scope.ConfigPath, true);
        FileManager.MigrateConfigIfNeeded(reloaded, "1");
        var reloadedSchemaVersion = reloaded.Bind("Version", "ConfigSchemaVersion", string.Empty).Value;
        reloaded.Save();
        var reloadedConfigText = File.ReadAllText(scope.ConfigPath);

        Assert.Equal("1", reloadedSchemaVersion);
        Assert.DoesNotContain("Current Version = 999", reloadedConfigText, StringComparison.Ordinal);
    }

    [Fact]
    public void MigrateConfigIfNeeded_OlderSchema_AppliesIntermediateMigrations()
    {
        using var scope = new TempConfigScope();
        var seedConfig = new ConfigFile(scope.ConfigPath, true);
        seedConfig.Bind("Version", "ConfigSchemaVersion", string.Empty).Value = "0";
        seedConfig.Bind("Gameplay", "UserSetting", 0).Value = 77;
        seedConfig.Save();

        var config = new ConfigFile(scope.ConfigPath, true);
        FileManager.MigrateConfigIfNeeded(config, "1");

        var schemaVersion = config.Bind("Version", "ConfigSchemaVersion", string.Empty).Value;
        var userSetting = config.Bind("Gameplay", "UserSetting", 0).Value;

        Assert.Equal("1", schemaVersion);
        Assert.Equal(77, userSetting);
    }

    [Fact]
    public void DefineConfig_WritesCanonicalVersionKeysOnly()
    {
        using var scope = new TempConfigScope();
        var config = new ConfigFile(scope.ConfigPath, true);
        config.Bind("Version", "ConfigSchemaVersion", "1", "Tracks config schema version for non-destructive migrations.");
        config.Bind("Version", "PluginVersion", "1.0.0", "Tracks plugin release version.");
        config.Save();

        var configText = File.ReadAllText(scope.ConfigPath);
        Assert.Contains("ConfigSchemaVersion =", configText, StringComparison.Ordinal);
        Assert.Contains("PluginVersion =", configText, StringComparison.Ordinal);
        Assert.DoesNotContain("Current Version =", configText, StringComparison.Ordinal);
    }


    [Fact]
    public void MigrateConfigIfNeeded_UsesDirectRemoveReflectionPathWhenAvailable()
    {
        using var scope = new TempConfigScope();
        var seedConfig = new ConfigFile(scope.ConfigPath, true);
        seedConfig.Bind("Version", "Current Version", string.Empty).Value = "1";
        seedConfig.Save();

        var config = new TrackingRemoveConfigFile(scope.ConfigPath, true);

        FileManager.MigrateConfigIfNeeded(config, "1");

        Assert.Equal(1, config.RemoveCalls);
        Assert.Equal(0, config.OrphanedEntriesAccesses);
    }

    [Fact]
    public void MigrateConfigIfNeeded_RemoveReflectionFailure_UsesOrphanedEntriesFallback()
    {
        using var scope = new TempConfigScope();
        var seedConfig = new ConfigFile(scope.ConfigPath, true);
        seedConfig.Bind("Version", "Current Version", string.Empty).Value = "1";
        seedConfig.Save();

        var config = new ThrowingRemoveFallbackConfigFile(scope.ConfigPath, true);
        Assert.True(config.HasLegacyEntry);

        FileManager.MigrateConfigIfNeeded(config, "1");

        Assert.Equal(1, config.RemoveCalls);
        Assert.True(config.OrphanedEntriesAccesses > 0);
        Assert.False(config.HasLegacyEntry);
    }

    [Fact]
    public void MigrateConfigIfNeeded_ReflectionCleanupFailure_StillNormalizesSchemaVersion()
    {
        using var scope = new TempConfigScope();
        var seedConfig = new ConfigFile(scope.ConfigPath, true);
        seedConfig.Bind("Version", "Current Version", string.Empty).Value = "1";
        seedConfig.Save();

        var config = new ThrowingLegacyCleanupConfigFile(scope.ConfigPath, true);

        FileManager.MigrateConfigIfNeeded(config, "1");

        var schemaVersion = config.Bind("Version", "ConfigSchemaVersion", string.Empty).Value;
        var configText = File.ReadAllText(scope.ConfigPath);

        Assert.Equal("1", schemaVersion);
        Assert.Contains("ConfigSchemaVersion = 1", configText, StringComparison.Ordinal);
        Assert.DoesNotContain("Current Version = 1", configText, StringComparison.Ordinal);
    }

    [Fact]
    public void MigrateConfigIfNeeded_TotalCleanupFailure_LogsSingleWarning()
    {
        using var scope = new TempConfigScope();
        var seedConfig = new ConfigFile(scope.ConfigPath, true);
        seedConfig.Bind("Version", "Current Version", string.Empty).Value = "1";
        seedConfig.Save();

        var config = new ThrowingLegacyCleanupConfigFile(scope.ConfigPath, true);
        File.Delete(scope.ConfigPath);

        using var loggerScope = new NetLoggerScope();
        FileManager.MigrateConfigIfNeeded(config, "1");

        Assert.Single(loggerScope.Warnings);
    }

    [Fact]
    public void MigrateConfigIfNeeded_DuplicateLegacyKeyOutsideVersionSection_IgnoresNonVersionSections()
    {
        using var scope = new TempConfigScope();
        File.WriteAllText(scope.ConfigPath,
            "[General]\n" +
            "Current Version = 99\n\n" +
            "[Version]\n" +
            "Current Version = 1\n");

        var config = new ConfigFile(scope.ConfigPath, true);
        FileManager.MigrateConfigIfNeeded(config, "1");

        var schemaVersion = config.Bind("Version", "ConfigSchemaVersion", string.Empty).Value;
        var configText = File.ReadAllText(scope.ConfigPath);

        Assert.Equal("1", schemaVersion);
        Assert.Contains("[General]", configText, StringComparison.Ordinal);
        Assert.Contains("Current Version = 99", configText, StringComparison.Ordinal);
        Assert.DoesNotContain("Current Version = 1", configText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Current Version=1")]
    [InlineData("Current Version    =    1")]
    [InlineData("    Current Version = 1")]
    public void MigrateConfigIfNeeded_LegacyVersionSpacingVariants_ParsesAndClearsOrRemovesKey(string legacyLine)
    {
        using var scope = new TempConfigScope();
        File.WriteAllText(scope.ConfigPath,
            "[Version]\n" +
            $"{legacyLine}\n");

        var config = new ConfigFile(scope.ConfigPath, true);
        FileManager.MigrateConfigIfNeeded(config, "1");

        var schemaVersion = config.Bind("Version", "ConfigSchemaVersion", string.Empty).Value;
        var configText = File.ReadAllText(scope.ConfigPath);

        Assert.Equal("1", schemaVersion);
        Assert.True(IsLegacyVersionClearedOrRemoved(configText));
    }


    [Theory]
    [InlineData("[VeRsIoN]", "Current Version")]
    [InlineData("[Version]", "cUrReNt vErSiOn")]
    [InlineData("[vErSiOn]", "CuRrEnT VeRsIoN")]
    public void MigrateConfigIfNeeded_LegacyVersionMixedCaseSectionOrKey_ParsesAndClearsOrRemovesKey(string sectionHeader, string legacyKey)
    {
        using var scope = new TempConfigScope();
        File.WriteAllText(scope.ConfigPath,
            $"{sectionHeader}\n" +
            $"{legacyKey} = 1\n");

        var config = new ConfigFile(scope.ConfigPath, true);
        FileManager.MigrateConfigIfNeeded(config, "1");

        var schemaVersion = config.Bind("Version", "ConfigSchemaVersion", string.Empty).Value;
        var configText = File.ReadAllText(scope.ConfigPath);

        Assert.Equal("1", schemaVersion);
        Assert.Contains("ConfigSchemaVersion = 1", configText, StringComparison.Ordinal);
        Assert.True(IsLegacyVersionClearedOrRemoved(configText));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("   ")]
    [InlineData("1.2")]
    [InlineData("1abc")]
    public void MigrateConfigIfNeeded_InvalidLegacySourceVersion_DefaultsToSchemaZeroAndCompletes(string legacyVersion)
    {
        using var scope = new TempConfigScope();
        File.WriteAllText(scope.ConfigPath,
            "[Version]\n" +
            $"Current Version = {legacyVersion}\n");

        var config = new ConfigFile(scope.ConfigPath, true);
        FileManager.MigrateConfigIfNeeded(config, "1");

        var schemaVersion = config.Bind("Version", "ConfigSchemaVersion", string.Empty).Value;
        var configText = File.ReadAllText(scope.ConfigPath);

        Assert.Equal("1", schemaVersion);
        Assert.Contains("ConfigSchemaVersion = 1", configText, StringComparison.Ordinal);
        Assert.True(IsLegacyVersionClearedOrRemoved(configText));
    }

    static bool IsLegacyVersionClearedOrRemoved(string configText)
    {
        var inVersionSection = false;
        foreach (var line in configText.Split('\n', StringSplitOptions.None))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal))
            {
                inVersionSection = string.Equals(trimmed[1..^1].Trim(), "Version", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inVersionSection || string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#", StringComparison.Ordinal) || trimmed.StartsWith(";", StringComparison.Ordinal))
                continue;

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
                continue;

            var key = line[..separatorIndex].Trim();
            if (!string.Equals(key, "Current Version", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!string.IsNullOrWhiteSpace(line[(separatorIndex + 1)..]))
                return false;
        }

        return true;
    }

    sealed class TrackingRemoveConfigFile : ConfigFile
    {
        internal int RemoveCalls { get; private set; }
        internal int OrphanedEntriesAccesses { get; private set; }

        internal TrackingRemoveConfigFile(string configPath, bool saveOnInit) : base(configPath, saveOnInit) { }

        public new bool Remove(ConfigDefinition definition)
        {
            RemoveCalls++;
            return true;
        }

        public new IDictionary OrphanedEntries
        {
            get
            {
                OrphanedEntriesAccesses++;
                return base.OrphanedEntries;
            }
        }
    }

    sealed class ThrowingRemoveFallbackConfigFile : ConfigFile
    {
        readonly IDictionary _orphanedEntries = new Hashtable();

        internal int RemoveCalls { get; private set; }
        internal int OrphanedEntriesAccesses { get; private set; }
        internal bool HasLegacyEntry => _orphanedEntries.Contains(new ConfigDefinition("Version", "Current Version"));

        internal ThrowingRemoveFallbackConfigFile(string configPath, bool saveOnInit) : base(configPath, saveOnInit)
        {
            _orphanedEntries[new ConfigDefinition("Version", "Current Version")] = "1";
        }

        public new bool Remove(ConfigDefinition definition)
        {
            RemoveCalls++;
            throw new InvalidOperationException("Simulated Remove reflection failure.");
        }

        public new IDictionary OrphanedEntries
        {
            get
            {
                OrphanedEntriesAccesses++;
                return _orphanedEntries;
            }
        }
    }

    sealed class ThrowingLegacyCleanupConfigFile : ConfigFile
    {
        internal int RemoveCalls { get; private set; }
        internal int OrphanedEntriesAccesses { get; private set; }

        internal ThrowingLegacyCleanupConfigFile(string configPath, bool saveOnInit) : base(configPath, saveOnInit) { }

        public new bool Remove(ConfigDefinition definition)
        {
            RemoveCalls++;
            throw new InvalidOperationException("Simulated Remove reflection failure.");
        }

        public new IDictionary OrphanedEntries
        {
            get
            {
                OrphanedEntriesAccesses++;
                throw new InvalidOperationException("Simulated OrphanedEntries reflection failure.");
            }
        }
    }

    sealed class TempConfigScope : IDisposable
    {
        readonly string _root = Path.Combine(Path.GetTempPath(), $"NetworkingLibrary_ConfigMigration_{Guid.NewGuid():N}");

        internal string ConfigPath => Path.Combine(_root, "config.cfg");

        internal TempConfigScope()
        {
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, true);
        }
    }

    sealed class NetLoggerScope : IDisposable
    {
        readonly FieldInfo? _loggerField = typeof(Net).GetField("<Logger>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic);
        readonly object? _originalLogger;
        readonly ManualLogSource _logger = new("FileManagerConfigMigrationTests");
        readonly TestLogListener _listener = new();

        internal IReadOnlyList<string> Warnings => _listener.Warnings;

        internal NetLoggerScope()
        {
            if (_loggerField == null)
                return;

            _originalLogger = _loggerField.GetValue(null);
            Logger.Listeners.Add(_listener);
            _loggerField.SetValue(null, _logger);
        }

        public void Dispose()
        {
            if (_loggerField != null)
                _loggerField.SetValue(null, _originalLogger);
            Logger.Listeners.Remove(_listener);
            _logger.Dispose();
        }
    }

    sealed class TestLogListener : ILogListener
    {
        readonly List<string> _warnings = new();
        internal IReadOnlyList<string> Warnings => _warnings;

        public void LogEvent(object sender, LogEventArgs eventArgs)
        {
            if (eventArgs.Level == LogLevel.Warning)
                _warnings.Add(eventArgs.Data?.ToString() ?? string.Empty);
        }

        public void Dispose() { }
    }
}
