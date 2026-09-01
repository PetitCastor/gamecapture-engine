using GameCapture.Engine.Plugins;
using Xunit;

namespace GameCapture.Engine.Tests;

/// <summary>
/// Buffer lifetime: when one appears, how long it lasts, and what a caller sees for a plugin that has
/// never run. The rule the rest of the feature leans on is that a buffer outlives its process, so a
/// plugin that died at startup can still be read from a row that now says "stopped".
/// </summary>
public class PluginLogStoreTests
{
    [Fact]
    public void Open_CreatesABufferThatHasReports()
    {
        var store = new PluginLogStore();

        Assert.False(store.Has("mission-plugin"));
        store.Open("mission-plugin");
        Assert.True(store.Has("mission-plugin"));
    }

    /// <summary>
    /// A relaunch must append to the existing history rather than replace it: the engine's "started"
    /// notice separates the runs, so "crashed, restarted, crashed again" stays readable as one story
    /// instead of silently erasing the first — and most informative — failure.
    /// </summary>
    [Fact]
    public void Open_Twice_ReusesTheSameBuffer()
    {
        var store = new PluginLogStore();
        store.Open("mission-plugin");
        store.Append("mission-plugin", PluginLogStream.Stdout, "first run");
        store.Open("mission-plugin");
        store.Append("mission-plugin", PluginLogStream.Stdout, "second run");

        var page = store.Read("mission-plugin", after: -1, limit: 100);

        Assert.Equal(["first run", "second run"], page.Lines.Select(line => line.Text));
    }

    [Fact]
    public void Drop_ForgetsOnePluginAndLeavesTheRest()
    {
        var store = new PluginLogStore();
        store.Open("mission-plugin");
        store.Open("refinery-plugin");

        store.Drop("mission-plugin");

        Assert.False(store.Has("mission-plugin"));
        Assert.True(store.Has("refinery-plugin"));
    }

    /// <summary>
    /// "This plugin has produced no output" is an ordinary answer, not a failure, so the endpoint above
    /// this can answer 200 with an empty page rather than a 404.
    /// </summary>
    [Fact]
    public void Read_ForAPluginThatNeverStarted_ReportsNoBuffer()
    {
        var page = new PluginLogStore().Read("mission-plugin", after: -1, limit: 100);

        Assert.False(page.HasBuffer);
        Assert.Empty(page.Lines);
    }

    [Fact]
    public void Append_ForAPluginThatNeverStarted_IsANoOp()
    {
        var store = new PluginLogStore();

        store.Append("mission-plugin", PluginLogStream.Engine, "-- stopped by the engine --");

        Assert.False(store.Has("mission-plugin"));
    }
}
