using NetworkingLibrary.Features;
using System.IO;
using Xunit;

namespace NetworkingLibrary.Tests;

public class FileManagerPathTests
{
    [Fact]
    public void BuildConfigPath_ComposesExpectedConfigFilePath()
    {
        var configFolderPath = Path.Combine("root", "DAa Mods", "SamplePlugin");

        var configPath = FileManager.BuildConfigPath(configFolderPath);

        Assert.Equal(Path.Combine("root", "DAa Mods", "SamplePlugin", "config.cfg"), configPath);
    }
}
