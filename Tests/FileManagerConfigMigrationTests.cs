using BepInEx.Configuration;
using NetworkingLibrary.Features;
using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using Xunit;

namespace NetworkingLibrary.Tests;

public class FileManagerConfigMigrationTests
{
    static readonly MethodInfo TryParseSchemaVersionMethod =
        typeof(FileManager).GetMethod("TryParseSchemaVersion", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("TryParseSchemaVersion method not found.");

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

    [Theory]
    [InlineData("0", 0)]
    [InlineData("1", 1)]
    [InlineData("10", 10)]
    public void TryParseSchemaVersion_ValidPlainIntegers_ReturnsTrue(string version, int expected)
    {
        var result = TryParseSchemaVersion(version, out var schemaVersion);

        Assert.True(result);
        Assert.Equal(expected, schemaVersion);
    }

    [Theory]
    [InlineData(" 1 ")]
    [InlineData("1.0")]
    [InlineData("-1")]
    public void TryParseSchemaVersion_InvalidFormattedValues_ReturnsFalse(string version)
    {
        var result = TryParseSchemaVersion(version, out var schemaVersion);

        Assert.False(result);
        Assert.Equal(0, schemaVersion);
    }

    [Fact]
    public void TryParseSchemaVersion_LocalizedDigitsAndGrouping_ReturnsFalse()
    {
        var arabicDigits = 123.ToString(new CultureInfo("ar-EG"));
        var groupedValue = 1000.ToString("N0", new CultureInfo("de-DE"));

        Assert.False(TryParseSchemaVersion(arabicDigits, out _));
        Assert.False(TryParseSchemaVersion(groupedValue, out _));
    }

    [Fact]
    public void MigrateConfigIfNeeded_CurrentSchemaWithValidValue_DoesNotChangeConfig()
    {
        using var scope = new TempConfigScope();
        var seedConfig = new ConfigFile(scope.ConfigPath, true);
        seedConfig.Bind("Version", "ConfigSchemaVersion", string.Empty).Value = "1";
        seedConfig.Bind("Version", "Current Version", string.Empty).Value = "0";
        seedConfig.Bind("Gameplay", "UserSetting", 0).Value = 88;
        seedConfig.Save();

        var config = new ConfigFile(scope.ConfigPath, true);
        FileManager.MigrateConfigIfNeeded(config, "1");

        Assert.Equal("1", config.Bind("Version", "ConfigSchemaVersion", string.Empty).Value);
        Assert.Equal("0", config.Bind("Version", "Current Version", string.Empty).Value);
        Assert.Equal(88, config.Bind("Gameplay", "UserSetting", 0).Value);
    }

    static bool TryParseSchemaVersion(string version, out int schemaVersion)
    {
        var args = new object[] { version, 0 };
        var result = (bool)(TryParseSchemaVersionMethod.Invoke(null, args)
            ?? throw new InvalidOperationException("TryParseSchemaVersion invocation failed."));
        schemaVersion = (int)args[1];
        return result;
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
