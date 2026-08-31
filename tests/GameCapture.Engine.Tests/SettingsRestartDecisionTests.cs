using GameCapture.Engine;
using Xunit;

namespace GameCapture.Engine.Tests;

public sealed class SettingsRestartDecisionTests
{
    [Fact]
    public void ThemeOnlyChange_DoesNotRequireRestart()
    {
        var changes = new Dictionary<string, object> { ["theme"] = "dark" };

        Assert.False(SettingsRestartDecision.IsRestartRequired(changes));
    }

    [Fact]
    public void ThemeAndScanIntervalChange_RequiresRestart()
    {
        var changes = new Dictionary<string, object>
        {
            ["theme"] = "dark",
            ["scanIntervalMs"] = 250,
        };

        Assert.True(SettingsRestartDecision.IsRestartRequired(changes));
    }

    [Fact]
    public void MonitorIndexOnlyChange_RequiresRestart()
    {
        var changes = new Dictionary<string, object> { ["monitorIndex"] = 1 };

        Assert.True(SettingsRestartDecision.IsRestartRequired(changes));
    }

    [Fact]
    public void EmptyChangeSet_DoesNotRequireRestart()
    {
        var changes = new Dictionary<string, object>();

        Assert.False(SettingsRestartDecision.IsRestartRequired(changes));
    }
}
