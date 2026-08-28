using GameCapture.Engine.Tray;
using Xunit;

namespace GameCapture.Engine.Tests;

/// <summary>
/// Pins <see cref="EngineSettings"/> value equality — the tray's no-op-save guard compares an edited
/// settings record against the seeded one to decide whether a change (and therefore a restart) is
/// needed, so same-valued records must compare equal and any differing field must compare unequal.
/// </summary>
public class EngineSettingsTests
{
    private static EngineSettings Baseline() => new(
        @"C:\dumps", "en-US", 500, "Ctrl+Shift+F12", "gamecapture", true, 1000, true);

    [Fact]
    public void Same_values_are_equal()
    {
        var a = Baseline();
        var b = Baseline();
        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData(@"C:\other", "en-US", 500, "Ctrl+Shift+F12", "gamecapture", true, 1000, true)]
    [InlineData(@"C:\dumps", "fr-FR", 500, "Ctrl+Shift+F12", "gamecapture", true, 1000, true)]
    [InlineData(@"C:\dumps", "en-US", 750, "Ctrl+Shift+F12", "gamecapture", true, 1000, true)]
    [InlineData(@"C:\dumps", "en-US", 500, "Ctrl+Alt+F12", "gamecapture", true, 1000, true)]
    [InlineData(@"C:\dumps", "en-US", 500, "Ctrl+Shift+F12", "other-pipe", true, 1000, true)]
    [InlineData(@"C:\dumps", "en-US", 500, "Ctrl+Shift+F12", "gamecapture", false, 1000, true)]
    [InlineData(@"C:\dumps", "en-US", 500, "Ctrl+Shift+F12", "gamecapture", true, 2000, true)]
    [InlineData(@"C:\dumps", "en-US", 500, "Ctrl+Shift+F12", "gamecapture", true, 1000, false)]
    public void Any_differing_field_is_unequal(
        string outputDir,
        string ocr,
        int scan,
        string hotkey,
        string pipeName,
        bool metricsEnabled,
        int metricsIntervalMs,
        bool trayEnabled)
    {
        Assert.NotEqual(Baseline(), new EngineSettings(
            outputDir, ocr, scan, hotkey, pipeName, metricsEnabled, metricsIntervalMs, trayEnabled));
    }
}
