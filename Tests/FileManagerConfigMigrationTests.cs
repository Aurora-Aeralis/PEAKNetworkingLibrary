using BepInEx.Configuration;
using NetworkingLibrary.Features;
using System;
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
    public void MigrateConfigIfNeeded_UsesRemovePathWhenAvailable()
    {
        using var scope = new TempConfigScope();
        var seedConfig = new ConfigFile(scope.ConfigPath, true);
        seedConfig.Bind("Version", "Current Version", string.Empty).Value = "1";
        seedConfig.Save();

        var config = new TrackingRemoveConfigFile(scope.ConfigPath, true);

        FileManager.MigrateConfigIfNeeded(config, "1");

        Assert.Equal(1, config.RemoveCalls);
    }

    [Fact]
    public void MigrateConfigIfNeeded_RemoveReflectionFailure_UsesFileRewriteFallback()
    {
        using var scope = new TempConfigScope();
        var seedConfig = new ConfigFile(scope.ConfigPath, true);
        seedConfig.Bind("Version", "Current Version", string.Empty).Value = "1";
        seedConfig.Save();

        var config = new ThrowingRemoveFallbackConfigFile(scope.ConfigPath, true);

        FileManager.MigrateConfigIfNeeded(config, "1");

        Assert.Equal(1, config.RemoveCalls);
        var legacyVersion = config.Bind("Version", "Current Version", string.Empty).Value;
        var configText = File.ReadAllText(scope.ConfigPath);
        Assert.Equal(string.Empty, legacyVersion);
        Assert.DoesNotContain("Current Version = 1", configText, StringComparison.Ordinal);
    }

    [Fact]
    public void MigrateConfigIfNeeded_RemovalPathUnavailable_MigratesByRewrite()
    {
        using var scope = new TempConfigScope();
        File.WriteAllText(scope.ConfigPath,
            "[Version]\n" +
            "Current Version = 1\n");

        var config = new MissingRemoveConfigFile(scope.ConfigPath, true);

        FileManager.MigrateConfigIfNeeded(config, "1");

        var schemaVersion = config.Bind("Version", "ConfigSchemaVersion", string.Empty).Value;
        var legacyVersion = config.Bind("Version", "Current Version", string.Empty).Value;
        var configText = File.ReadAllText(scope.ConfigPath);

        Assert.Equal("1", schemaVersion);
        Assert.Equal(string.Empty, legacyVersion);
        Assert.Contains("ConfigSchemaVersion = 1", configText, StringComparison.Ordinal);
        Assert.DoesNotContain("Current Version = 1", configText, StringComparison.Ordinal);
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
    [InlineData("; Current Version = 99\nCurrent Version = 1")]
    [InlineData("# Current Version = 99\nCurrent Version = 1")]
    public void MigrateConfigIfNeeded_LegacyVersionSpacingAndCommentVariants_ParsesAndClearsOrRemovesKey(string legacyLine)
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

    [Fact]
    public void MigrateConfigIfNeeded_ReRun_IsIdempotent()
    {
        using var scope = new TempConfigScope();
        File.WriteAllText(scope.ConfigPath,
            "[Version]\n" +
            "Current Version = 1\n");

        var config = new ConfigFile(scope.ConfigPath, true);
        FileManager.MigrateConfigIfNeeded(config, "1");
        var once = File.ReadAllText(scope.ConfigPath);

        var reloaded = new ConfigFile(scope.ConfigPath, true);
        FileManager.MigrateConfigIfNeeded(reloaded, "1");
        var twice = File.ReadAllText(scope.ConfigPath);

        Assert.Equal(once, twice);
    }

    [Fact]
    public void MigrateConfigIfNeeded_MissingConfigAndRemovalUnavailable_DoesNotCreateSchemaEntry()
    {
        using var scope = new TempConfigScope();
        if (File.Exists(scope.ConfigPath))
            File.Delete(scope.ConfigPath);

        var config = new MissingRemoveConfigFile(scope.ConfigPath, false);
        if (File.Exists(scope.ConfigPath))
            File.Delete(scope.ConfigPath);

        FileManager.MigrateConfigIfNeeded(config, "1");

        var schemaVersion = config.Bind("Version", "ConfigSchemaVersion", string.Empty).Value;
        Assert.Equal(string.Empty, schemaVersion);
    }

    static bool IsLegacyVersionClearedOrRemoved(string configText)
    {
        var inVersionSection = false;
        foreach (var line in configText.Split('\n', StringSplitOptions.None))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal))
            {
                inVersionSection = string.Equals(trimmed[1..^1].Trim(), "Version", StringComparison.Ordinal);
                continue;
            }

            if (!inVersionSection || string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#", StringComparison.Ordinal) || trimmed.StartsWith(";", StringComparison.Ordinal))
                continue;

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
                continue;

            var key = line[..separatorIndex].Trim();
            if (!string.Equals(key, "Current Version", StringComparison.Ordinal))
                continue;

            if (!string.IsNullOrWhiteSpace(line[(separatorIndex + 1)..]))
                return false;
        }

        return true;
    }

    sealed class TrackingRemoveConfigFile : ConfigFile
    {
        internal int RemoveCalls { get; private set; }

        internal TrackingRemoveConfigFile(string configPath, bool saveOnInit) : base(configPath, saveOnInit) { }

        public new bool Remove(ConfigDefinition definition)
        {
            RemoveCalls++;
            return true;
        }
    }

    sealed class ThrowingRemoveFallbackConfigFile : ConfigFile
    {
        internal int RemoveCalls { get; private set; }

        internal ThrowingRemoveFallbackConfigFile(string configPath, bool saveOnInit) : base(configPath, saveOnInit)
        { }

        public new bool Remove(ConfigDefinition definition)
        {
            RemoveCalls++;
            throw new InvalidOperationException("Simulated Remove reflection failure.");
        }
    }

    sealed class MissingRemoveConfigFile : ConfigFile
    {
        internal MissingRemoveConfigFile(string configPath, bool saveOnInit) : base(configPath, saveOnInit) { }
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
