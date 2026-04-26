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
        var legacyVersion = config.Bind("Version", "Current Version", string.Empty).Value;

        Assert.Equal("1", schemaVersion);
        Assert.True(string.IsNullOrWhiteSpace(legacyVersion));
    }

    [Fact]
    public void MigrateConfigIfNeeded_LegacyVersionOnly_NormalizesToSchemaVersion()
    {
        using var scope = new TempConfigScope();
        var seedConfig = new ConfigFile(scope.ConfigPath, true);
        seedConfig.Bind("Version", "Current Version", string.Empty).Value = "1";
        seedConfig.Save();

        var config = new ConfigFile(scope.ConfigPath, true);
        FileManager.MigrateConfigIfNeeded(config, "1");

        var schemaVersion = config.Bind("Version", "ConfigSchemaVersion", string.Empty).Value;
        var legacyVersion = config.Bind("Version", "Current Version", string.Empty).Value;

        Assert.Equal("1", schemaVersion);
        Assert.Equal("1", legacyVersion);
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
