using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Xunit;

namespace GameCapture.Engine.Tests;

/// <summary>
/// Proves every crop leaving <see cref="OcrPipeline.CropAndScaleAsync"/> has its B and G channels
/// overwritten with R, not just that red content survives. The chromatic-aberration fix depends on
/// Windows OCR's internal luma collapse landing on a pure red-channel value rather than an R/G/B
/// average, so a test that only checked "still looks red" would pass even if the overwrite silently
/// stopped happening.
/// </summary>
public class OcrPipelineRedChannelGrayscaleTests
{
    private const int FrameWidth = 16;
    private const int FrameHeight = 16;

    private static readonly BitmapBounds WholeFrame =
        new() { X = 0, Y = 0, Width = FrameWidth, Height = FrameHeight };

    private static IBuffer ToBuffer(byte[] bytes)
    {
        var writer = new DataWriter();
        writer.WriteBytes(bytes);
        return writer.DetachBuffer();
    }

    private static byte[] ToBytes(SoftwareBitmap bitmap)
    {
        var buffer = new Windows.Storage.Streams.Buffer((uint)(bitmap.PixelWidth * bitmap.PixelHeight * 4));
        bitmap.CopyToBuffer(buffer);
        var bytes = new byte[buffer.Length];
        using var reader = DataReader.FromBuffer(buffer);
        reader.ReadBytes(bytes);
        return bytes;
    }

    /// <summary>Every pixel carries a distinct B/G/R so an overwrite is unmistakable, none of them equal.</summary>
    private static SoftwareBitmap MakeFrameWithDistinctChannels()
    {
        var bytes = new byte[FrameWidth * FrameHeight * 4];
        for (var y = 0; y < FrameHeight; y++)
        {
            for (var x = 0; x < FrameWidth; x++)
            {
                var i = (y * FrameWidth + x) * 4;
                bytes[i] = 150;     // B
                bytes[i + 1] = 200; // G
                bytes[i + 2] = 10;  // R
                bytes[i + 3] = 255; // A
            }
        }

        var frame = new SoftwareBitmap(BitmapPixelFormat.Bgra8, FrameWidth, FrameHeight, BitmapAlphaMode.Ignore);
        frame.CopyFromBuffer(ToBuffer(bytes));
        return frame;
    }

    [Fact]
    public async Task CropAndScale_EveryPixel_HasBAndGOverwrittenWithR()
    {
        var ocr = new OcrPipeline();
        using var frame = MakeFrameWithDistinctChannels();

        using var crop = await ocr.CropAndScaleAsync(frame, WholeFrame, 3.0);
        var bytes = ToBytes(crop);

        for (var i = 0; i < bytes.Length; i += 4)
        {
            var b = bytes[i];
            var g = bytes[i + 1];
            var r = bytes[i + 2];
            Assert.True(b == r && g == r,
                $"pixel at byte {i}: expected B==G==R, got B={b} G={g} R={r}");
        }
    }

    [Fact]
    public async Task CropAndScale_AtOneToOne_ReplicatesTheSourceRedValueExactly()
    {
        // Scale 1.0 skips Cubic interpolation's smoothing, so the source R value should survive
        // into the grayscale output unchanged rather than merely "close".
        var ocr = new OcrPipeline();
        using var frame = MakeFrameWithDistinctChannels();

        using var crop = await ocr.CropAndScaleAsync(frame, WholeFrame, 1.0);
        var bytes = ToBytes(crop);

        for (var i = 0; i < bytes.Length; i += 4)
            Assert.Equal(10, bytes[i + 2]);
    }
}
