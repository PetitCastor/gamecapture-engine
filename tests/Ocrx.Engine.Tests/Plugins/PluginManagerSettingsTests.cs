using Ocrx.Engine.Plugins;
using Xunit;

namespace Ocrx.Engine.Tests.Plugins;

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

    /// <summary>A document written before auto-start existed carries no list, and must read as the
    /// default rather than as "the user opted every plugin out".</summary>
    [Fact]
    public void DocumentWithoutAnAutoStartList_LeavesEveryPluginOn()
    {
        File.WriteAllText(EnsuredSettingsFile(), """{"includePreviews":true}""");

        Assert.True(PluginManagerSettings.Load(EnsuredSettingsFile()).IsAutoStartEnabled("mission-plugin"));
    }

    [Fact]
    public void AutoStart_IsOnForAPluginNothingWasRecordedFor()
        => Assert.True(PluginManagerSettings.Load(PluginPaths.SettingsFile(_root)).IsAutoStartEnabled("mission-plugin"));

    [Fact]
    public void DisabledAutoStart_ComesBackOnLoad()
    {
        var settings = PluginManagerSettings.Load(PluginPaths.SettingsFile(_root));
        settings.SetAutoStart("mission-plugin", false);
        settings.Save();

        var reloaded = PluginManagerSettings.Load(PluginPaths.SettingsFile(_root));
        Assert.False(reloaded.IsAutoStartEnabled("mission-plugin"));
        Assert.True(reloaded.IsAutoStartEnabled("refinery-plugin"));
    }

    [Fact]
    public void ReEnabledAutoStart_ComesBackOnLoad()
    {
        var settings = PluginManagerSettings.Load(PluginPaths.SettingsFile(_root));
        settings.SetAutoStart("mission-plugin", false);
        settings.Save();

        settings = PluginManagerSettings.Load(PluginPaths.SettingsFile(_root));
        settings.SetAutoStart("mission-plugin", true);
        settings.Save();

        Assert.True(PluginManagerSettings.Load(PluginPaths.SettingsFile(_root)).IsAutoStartEnabled("mission-plugin"));
        Assert.Empty(PluginManagerSettings.Load(PluginPaths.SettingsFile(_root)).AutoStartDisabledIds);
    }

    /// <summary>Both preferences share one document, so writing either must not drop the other.</summary>
    [Fact]
    public void SavingAutoStart_KeepsThePreviewPreference()
    {
        var settings = PluginManagerSettings.Load(PluginPaths.SettingsFile(_root));
        settings.IncludePreviews = true;
        settings.SetAutoStart("mission-plugin", false);
        settings.Save();

        var reloaded = PluginManagerSettings.Load(PluginPaths.SettingsFile(_root));
        Assert.True(reloaded.IncludePreviews);
        Assert.False(reloaded.IsAutoStartEnabled("mission-plugin"));
    }

    private string EnsuredSettingsFile()
    {
        Directory.CreateDirectory(_root);
        return PluginPaths.SettingsFile(_root);
    }
}
