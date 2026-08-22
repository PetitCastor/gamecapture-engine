using GameCapture.Sdk;
using GameCapture.Sdk.Overlay;

namespace GameCapture.Sdk.Overlay.Tests;

public class OverlaySinkFactoryTests
{
    [Fact]
    public async Task Create_OffWindows_ReturnsSilentNoOp()
    {
        if (OperatingSystem.IsWindows())
            return;

        var output = new RecordingPluginOutput();
        await using var sink = OverlaySinkFactory.Create(new OverlaySpec(), output);

        Assert.IsType<NoOpRecordSink>(sink);
        Assert.Empty(output.Lines);
        await sink.EmitAsync(
            new CaptureRecord(DateTime.UnixEpoch, "test", TriggerKind.Auto, "value"),
            CancellationToken.None);
    }
}
