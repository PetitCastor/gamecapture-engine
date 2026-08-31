using System.Text.Json;
using GameCapture.Engine;
using GameCapture.Engine.Tray;
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
            Assert.Equal(EngineTheme.System, config.Theme);
            Assert.True(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("\"light\"", EngineTheme.Light)]
    [InlineData("\"LIGHT\"", EngineTheme.Light)]
    [InlineData("\"Dark\"", EngineTheme.Dark)]
    [InlineData("\"system\"", EngineTheme.System)]
    [InlineData("\"neon\"", EngineTheme.System)]
    [InlineData("42", EngineTheme.System)]
    public void Load_ParsesThemeCaseInsensitivelyAndNeverFailsOnAnUnknownValue(string themeJson, EngineTheme expected)
    {
        var directory = Path.Combine(Path.GetTempPath(), "GameCapture.Engine.Tests", Guid.NewGuid().ToString());
        var path = Path.Combine(directory, "engine-config.json");

        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, $$"""{ "hotkey": "Ctrl+Shift+F12", "theme": {{themeJson}} }""");

            var config = EngineConfig.Load(path);

            Assert.Equal(expected, config.Theme);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Load_OffersThemeToAConfigFileThatPredatesIt()
    {
        // A file from before TASK-UI-02 has no "theme" key at all — the seed path must add one so
        // the setting is discoverable without documentation, same as a brand-new file gets it.
        var directory = Path.Combine(Path.GetTempPath(), "GameCapture.Engine.Tests", Guid.NewGuid().ToString());
        var path = Path.Combine(directory, "engine-config.json");

        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, """{ "hotkey": "Ctrl+Shift+F12" }""");

            var config = EngineConfig.Load(path);

            Assert.Equal(EngineTheme.System, config.Theme);
            var onDisk = File.ReadAllText(path);
            Assert.Contains("\"theme\"", onDisk);
            Assert.Contains("\"configVersion\"", onDisk);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Serialize_WritesThemeLowercase()
    {
        var config = new EngineConfig { Theme = EngineTheme.Dark };

        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        });

        Assert.Contains("\"theme\": \"dark\"", json);
    }

    [Fact]
    public void Load_NeverResurrectsADeliberatelyRemovedTheme()
    {
        // Already stamped past the version that introduced "theme" but the key is missing: the user
        // removed it (or it defaulted away) after already being offered it once, and re-adding it on
        // every later load is exactly the bug addedIn tagging exists to avoid.
        var directory = Path.Combine(Path.GetTempPath(), "GameCapture.Engine.Tests", Guid.NewGuid().ToString());
        var path = Path.Combine(directory, "engine-config.json");

        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, """{ "hotkey": "Ctrl+Shift+F12", "configVersion": 1 }""");

            var config = EngineConfig.Load(path);

            Assert.Equal(EngineTheme.System, config.Theme);
            Assert.DoesNotContain("\"theme\"", File.ReadAllText(path));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
