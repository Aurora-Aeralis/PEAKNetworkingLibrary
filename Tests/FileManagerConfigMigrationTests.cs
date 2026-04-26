using BepInEx.Configuration;
using NetworkingLibrary.Features;
using System;
using System.Collections;
using System.IO;
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
        seedConfig.Bind("Gameplay", "UserSetting", 0).Value = 12;
        seedConfig.Save();

        var config = new ConfigFile(scope.ConfigPath, true);
        FileManager.MigrateConfigIfNeeded(config, "1");

        var schemaVersion = config.Bind("Version", "ConfigSchemaVersion", string.Empty).Value;
        var userSetting = config.Bind("Gameplay", "UserSetting", 0).Value;
        var configText = File.ReadAllText(scope.ConfigPath);

        Assert.Equal("1", schemaVersion);
        Assert.Equal(12, userSetting);
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
    public void MigrateConfigIfNeeded_AlreadyCurrentWithoutLegacy_DoesNotWriteConfig()
    {
        using var scope = new TempConfigScope();
        var seedConfig = new ConfigFile(scope.ConfigPath, true);
        seedConfig.Bind("Version", "ConfigSchemaVersion", string.Empty).Value = "1";
        seedConfig.Bind("Gameplay", "UserSetting", 0).Value = 77;
        seedConfig.Save();

        var baseline = File.ReadAllText(scope.ConfigPath);
        var baselineStamp = File.GetLastWriteTimeUtc(scope.ConfigPath);
        System.Threading.Thread.Sleep(1100);

        var config = new ConfigFile(scope.ConfigPath, true);
        FileManager.MigrateConfigIfNeeded(config, "1");

        var afterText = File.ReadAllText(scope.ConfigPath);
        var afterStamp = File.GetLastWriteTimeUtc(scope.ConfigPath);

        Assert.Equal(baseline, afterText);
        Assert.Equal(baselineStamp, afterStamp);
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
        internal ThrowingLegacyCleanupConfigFile(string configPath, bool saveOnInit) : base(configPath, saveOnInit) { }

        public new bool Remove(ConfigDefinition definition)
        {
            throw new InvalidOperationException("Simulated Remove reflection failure.");
        }

        public new IDictionary OrphanedEntries => throw new InvalidOperationException("Simulated OrphanedEntries reflection failure.");
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
}
