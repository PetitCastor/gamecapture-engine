using GameCapture.Sdk;
using GameCapture.Sdk.Overlay;

namespace GameCapture.Sdk.Overlay.Tests;

public class OverlayRecordSinkTests
{
    [Fact]
    public async Task Observation_RendersFieldsAndResetsLinger()
    {
        var window = new FakeOverlayWindow();
        await using var sink = new OverlayRecordSink(
            new OverlaySpec { Template = "{ore}  ({signature})", LingerMs = 2500 }, window);
        var record = Record("raw fallback", new Dictionary<string, string>
        {
            ["ore"] = "Bexalite",
            ["signature"] = "2.14",
        });

        await sink.EmitAsync(record, CancellationToken.None);

        Assert.Equal(1, window.StartCount);
        var shown = Assert.Single(window.Shows);
        Assert.Equal("Bexalite  (2.14)", shown.Text);
        Assert.Equal(TimeSpan.FromMilliseconds(2500), shown.Linger);
    }

    [Fact]
    public async Task MissingTemplateField_FallsBackToRawText()
    {
        var window = new FakeOverlayWindow();
        await using var sink = new OverlayRecordSink(
            new OverlaySpec { Template = "{ore} ({missing})" }, window);

        await sink.EmitAsync(Record("raw fallback", new Dictionary<string, string>
        {
            ["ore"] = "Bexalite",
        }), CancellationToken.None);

        Assert.Equal("raw fallback", Assert.Single(window.Shows).Text);
    }

    [Fact]
    public async Task BlankTemplate_UsesRawTextAndZeroLingerStaysDisabled()
    {
        var window = new FakeOverlayWindow();
        await using var sink = new OverlayRecordSink(new OverlaySpec { LingerMs = 0 }, window);

        await sink.EmitAsync(Record("raw text"), CancellationToken.None);

        var shown = Assert.Single(window.Shows);
        Assert.Equal("raw text", shown.Text);
        Assert.Equal(TimeSpan.Zero, shown.Linger);
    }

    [Fact]
    public async Task Cleared_HidesWithoutRendering()
    {
        var window = new FakeOverlayWindow();
        await using var sink = new OverlayRecordSink(new OverlaySpec(), window);
        var record = Record("") with { Kind = RecordKind.Cleared };

        await sink.EmitAsync(record, CancellationToken.None);

        Assert.Equal(1, window.HideCount);
        Assert.Empty(window.Shows);
    }

    [Fact]
    public async Task DisposeAsync_DisposesTheWindow()
    {
        var window = new FakeOverlayWindow();
        var sink = new OverlayRecordSink(new OverlaySpec(), window);

        await sink.DisposeAsync();

        Assert.True(window.IsDisposed);
    }

    [Fact]
    public void InvalidSize_ThrowsBeforeTheWindowStarts()
    {
        var window = new FakeOverlayWindow();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new OverlayRecordSink(new OverlaySpec { Width = 0 }, window));
        Assert.Equal(0, window.StartCount);
    }

    private static CaptureRecord Record(string rawText,
        IReadOnlyDictionary<string, string>? fields = null)
        => new(DateTime.UnixEpoch, "refinery", TriggerKind.Auto, rawText) { Fields = fields };
}
