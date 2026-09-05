using Windows.Graphics.Imaging;
using Xunit;

namespace Ocrx.Engine.Tests;

/// <summary>
/// Replay order is the contract: the corpus is chronological only because FrameSaver names are
/// timestamped and the source sorts ordinally. A culture-aware or filesystem-order enumeration
/// would still produce ticks, just in an order that quietly changes what a state machine decides.
/// </summary>
public class ReplayFrameSourceTests
{
    [Fact]
    public async Task ReadFrameAsync_ReturnsFramesInOrdinalOrderThenEndOfStream()
    {
        using var source = new ReplayFrameSource(EngineTestFixtures.ReplayDir);

        var seen = new List<string>();
        while (true)
        {
            var read = await source.ReadFrameAsync(CancellationToken.None);
            if (read.Status == FrameReadStatus.EndOfStream)
                break;

            Assert.Equal(FrameReadStatus.FrameReady, read.Status);
            using var bitmap = read.Bitmap;
            Assert.NotNull(bitmap);
            // Bgra8 is what every downstream consumer assumes: the OCR crop path, the pixel
            // strip's 4-bytes-per-pixel arithmetic, and PNG dumps alike.
            Assert.Equal(BitmapPixelFormat.Bgra8, bitmap!.BitmapPixelFormat);

            seen.Add(source.LastFrameName!);
        }

        // Guard against the whole suite passing vacuously if the fixtures ever stop being copied
        // next to the test assembly: every assertion below is trivially true over zero frames.
        Assert.NotEmpty(seen);
        Assert.Equal(EngineTestFixtures.ExpectedFrameNames(), seen);
        Assert.Equal(seen.Count, source.FrameCount);

        // Exhausted stays exhausted — the scan loop reads end-of-stream as "corpus finished" and breaks.
        var afterEnd = await source.ReadFrameAsync(CancellationToken.None);
        Assert.Equal(FrameReadStatus.EndOfStream, afterEnd.Status);
        Assert.Null(afterEnd.Bitmap);
    }
}
