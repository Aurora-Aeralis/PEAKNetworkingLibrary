using BepInEx.Configuration;
using NetworkingLibrary.Features;
using System;
using System.IO;
using Xunit;

namespace NetworkingLibrary.Tests;

public class FileManagerConfigMigrationTests
{
    [Fact]
    public void MigrateConfigIfNeeded_PreservesUserValuesAcrossVersionBump()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"NetworkingLibrary_ConfigMigration_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var configPath = Path.Combine(tempRoot, "config.cfg");

        try
        {
            var seedConfig = new ConfigFile(configPath, true);
            seedConfig.Bind("Version", "Current Version", "1.0.0").Value = "1.0.0";
            seedConfig.Bind("Gameplay", "UserSetting", 99).Value = 77;
            seedConfig.Save();

            var migratedConfig = new ConfigFile(configPath, true);
            FileManager.MigrateConfigIfNeeded(migratedConfig, "2.0.0");

            var userSetting = migratedConfig.Bind("Gameplay", "UserSetting", 0).Value;
            var schemaVersion = migratedConfig.Bind("Version", "ConfigSchemaVersion", string.Empty).Value;
            var legacyVersion = migratedConfig.Bind("Version", "Current Version", string.Empty).Value;

            Assert.Equal(77, userSetting);
            Assert.Equal("2.0.0", schemaVersion);
            Assert.Equal("1.0.0", legacyVersion);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, true);
        }
    }
}
