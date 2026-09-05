using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Xunit;

namespace Ocrx.Engine.Tests;

/// <summary>
/// <see cref="OcrPipeline.ApplyRedChannelGrayscale"/> is called only from the OCR read paths, never
/// from <see cref="OcrPipeline.CropAndScaleAsync"/> itself — that method is a shared true-color
/// utility also consumed by non-OCR callers (pixel-sampling reads, ROI debug/corpus dumps). These
/// tests cover both halves of that split: the transform itself, in isolation, and a regression guard
/// proving <c>CropAndScaleAsync</c> keeps returning true color.
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

    /// <summary>Every pixel carries a distinct B/G/R so an overwrite (or a missing one) is unmistakable.</summary>
    private static SoftwareBitmap MakeBitmapWithDistinctChannels(int width, int height)
    {
        var bytes = new byte[width * height * 4];
        for (var i = 0; i < bytes.Length; i += 4)
        {
            bytes[i] = 150;     // B
            bytes[i + 1] = 200; // G
            bytes[i + 2] = 10;  // R
            bytes[i + 3] = 255; // A
        }

        var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Ignore);
        bitmap.CopyFromBuffer(ToBuffer(bytes));
        return bitmap;
    }

    [Fact]
    public void ApplyRedChannelGrayscale_EveryPixel_HasBAndGOverwrittenWithR()
    {
        using var bitmap = MakeBitmapWithDistinctChannels(FrameWidth, FrameHeight);

        OcrPipeline.ApplyRedChannelGrayscale(bitmap);
        var bytes = ToBytes(bitmap);

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
    public void ApplyRedChannelGrayscale_PreservesTheOriginalRedValueAndAlpha()
    {
        using var bitmap = MakeBitmapWithDistinctChannels(FrameWidth, FrameHeight);

        OcrPipeline.ApplyRedChannelGrayscale(bitmap);
        var bytes = ToBytes(bitmap);

        for (var i = 0; i < bytes.Length; i += 4)
        {
            Assert.Equal(10, bytes[i + 2]);  // R untouched
            Assert.Equal(255, bytes[i + 3]); // A untouched
        }
    }

    [Fact]
    public async Task CropAndScaleAsync_DoesNotApplyTheGrayscaleItself()
    {
        // Regression guard: CropAndScaleAsync is a shared true-color crop/scale utility consumed
        // by non-OCR callers (pixel sampling, ROI debug dumps) that must not receive an OCR-only
        // preprocessing step. Only the OCR read paths call ApplyRedChannelGrayscale explicitly.
        var ocr = new OcrPipeline();
        using var frame = MakeBitmapWithDistinctChannels(FrameWidth, FrameHeight);

        using var crop = await ocr.CropAndScaleAsync(frame, WholeFrame, 3.0);
        var bytes = ToBytes(crop);

        for (var i = 0; i < bytes.Length; i += 4)
        {
            var b = bytes[i];
            var g = bytes[i + 1];
            var r = bytes[i + 2];
            Assert.False(b == r && g == r,
                $"pixel at byte {i}: true color was collapsed to grayscale (B={b} G={g} R={r})");
        }
    }
}
