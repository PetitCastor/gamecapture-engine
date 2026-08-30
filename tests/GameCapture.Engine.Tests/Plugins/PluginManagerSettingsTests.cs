using GameCapture.Engine.Plugins;
using Xunit;

namespace GameCapture.Engine.Tests.Plugins;

public class PluginManagerSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gc-plugin-settings-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void MissingDocument_DefaultsToStableOnly()
        => Assert.False(PluginManagerSettings.Load(PluginPaths.SettingsFile(_root)).IncludePreviews);

    [Fact]
    public void SavedPreviewPreference_ComesBackOnLoad()
    {
        var settings = PluginManagerSettings.Load(PluginPaths.SettingsFile(_root));
        settings.IncludePreviews = true;
        settings.Save();

        Assert.True(PluginManagerSettings.Load(PluginPaths.SettingsFile(_root)).IncludePreviews);
    }
}
