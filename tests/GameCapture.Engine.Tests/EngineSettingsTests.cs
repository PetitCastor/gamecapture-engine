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
    [Fact]
    public void Same_values_are_equal()
    {
        var a = new EngineSettings(@"C:\dumps", "en-US", 500);
        var b = new EngineSettings(@"C:\dumps", "en-US", 500);
        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData(@"C:\other", "en-US", 500)]
    [InlineData(@"C:\dumps", "fr-FR", 500)]
    [InlineData(@"C:\dumps", "en-US", 750)]
    public void Any_differing_field_is_unequal(string outputDir, string ocr, int scan)
    {
        var baseline = new EngineSettings(@"C:\dumps", "en-US", 500);
        Assert.NotEqual(baseline, new EngineSettings(outputDir, ocr, scan));
    }
}
