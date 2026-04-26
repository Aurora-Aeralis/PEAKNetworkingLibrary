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
    public void MigrateConfigIfNeeded_LegacyKeyOutsideVersionSection_Ignored()
    {
        using var scope = new TempConfigScope();
        File.WriteAllText(scope.ConfigPath, """
[Gameplay]
Current Version = 9
""");

        var config = new ConfigFile(scope.ConfigPath, true);
        FileManager.MigrateConfigIfNeeded(config, "1");

        var schemaVersion = config.Bind("Version", "ConfigSchemaVersion", string.Empty).Value;
        Assert.Equal("1", schemaVersion);
    }

    [Fact]
    public void MigrateConfigIfNeeded_LegacyKeyWithWhitespace_ReadsValue()
    {
        using var scope = new TempConfigScope();
        File.WriteAllText(scope.ConfigPath, """
[Version]
Current Version     =      1
""");

        var config = new ConfigFile(scope.ConfigPath, true);
        FileManager.MigrateConfigIfNeeded(config, "1");

        var schemaVersion = config.Bind("Version", "ConfigSchemaVersion", string.Empty).Value;
        var configText = File.ReadAllText(scope.ConfigPath);

        Assert.Equal("1", schemaVersion);
        Assert.DoesNotContain("Current Version = 1", configText, StringComparison.Ordinal);
    }

    [Fact]
    public void MigrateConfigIfNeeded_MissingLegacyKey_TreatedAsEmpty()
    {
        using var scope = new TempConfigScope();
        File.WriteAllText(scope.ConfigPath, """
[Version]
PluginVersion = 1.2.3
""");

        var config = new ConfigFile(scope.ConfigPath, true);
        FileManager.MigrateConfigIfNeeded(config, "1");

        var schemaVersion = config.Bind("Version", "ConfigSchemaVersion", string.Empty).Value;
        Assert.Equal("1", schemaVersion);
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
