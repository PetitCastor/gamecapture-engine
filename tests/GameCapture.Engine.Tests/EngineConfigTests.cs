using GameCapture.Engine;
using Xunit;

namespace GameCapture.Engine.Tests;

public sealed class EngineConfigTests
{
    [Fact]
    public void GetDefaultPath_UsesTheLocalApplicationDataGameCaptureDirectory()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GameCapture",
            "engine-config.json");

        Assert.Equal(expected, EngineConfig.GetDefaultPath());
    }

    [Fact]
    public void Load_CreatesParentDirectoryAndDefaultConfig()
    {
        var directory = Path.Combine(Path.GetTempPath(), "GameCapture.Engine.Tests", Guid.NewGuid().ToString());
        var path = Path.Combine(directory, "engine-config.json");

        try
        {
            var config = EngineConfig.Load(path);

            Assert.Equal("Ctrl+Shift+F12", config.Hotkey);
            Assert.True(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
