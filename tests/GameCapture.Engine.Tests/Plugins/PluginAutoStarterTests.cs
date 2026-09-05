using GameCapture.Engine.Plugins;
using Xunit;

namespace GameCapture.Engine.Tests.Plugins;

/// <summary>
/// Covers the choice of what starts with the engine, not the launching itself: <see cref="Select"/>
/// is the half that has decisions in it, and exercising the other half would mean spawning real
/// child processes for the sake of asserting what <c>PluginLauncher</c> already has tests for.
/// </summary>
public class PluginAutoStarterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gc-autostart-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void InstalledPluginWithNoRecordedPreference_StartsWithTheEngine()
    {
        var state = StateWith("mission-plugin");

        var selected = PluginAutoStarter.Select(state, Settings());

        Assert.Equal(["mission-plugin"], selected.Select(plugin => plugin.Id));
    }

    [Fact]
    public void PluginTheUserTurnedOff_IsNotStarted()
    {
        var state = StateWith("mission-plugin", "refinery-plugin");
        var settings = Settings();
        settings.SetAutoStart("mission-plugin", false);

        var selected = PluginAutoStarter.Select(state, settings);

        Assert.Equal(["refinery-plugin"], selected.Select(plugin => plugin.Id));
    }

    [Fact]
    public void TurningAPluginBackOn_StartsItAgain()
    {
        var state = StateWith("mission-plugin");
        var settings = Settings();
        settings.SetAutoStart("mission-plugin", false);
        settings.SetAutoStart("mission-plugin", true);

        Assert.Single(PluginAutoStarter.Select(state, settings));
    }

    [Fact]
    public void NothingInstalled_StartsNothing()
        => Assert.Empty(PluginAutoStarter.Select(StateWith(), Settings()));

    /// <summary>The console line the pass writes is the only account of what started, so the order it
    /// reports must not depend on dictionary iteration.</summary>
    [Fact]
    public void Selection_IsOrderedById()
    {
        var state = StateWith("signature-plugin", "mission-plugin", "refinery-plugin");

        Assert.Equal(
            ["mission-plugin", "refinery-plugin", "signature-plugin"],
            PluginAutoStarter.Select(state, Settings()).Select(plugin => plugin.Id));
    }

    private PluginManagerSettings Settings() => PluginManagerSettings.Load(PluginPaths.SettingsFile(_root));

    private PluginInstallState StateWith(params string[] ids)
    {
        var state = PluginInstallState.Load(PluginPaths.StateFile(_root));
        foreach (var id in ids)
        {
            state.Set(new InstalledPlugin(
                id,
                id,
                "v1.0.0",
                Path.Combine(_root, id, $"{id}.exe"),
                DateTimeOffset.UtcNow));
        }

        return state;
    }
}
