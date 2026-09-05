using BenchmarkDotNet.Attributes;
using Ocrx.Contracts.Proto;
using Google.Protobuf;
using Windows.Graphics.Imaging;

namespace Ocrx.Engine.Benchmarks;

[MemoryDiagnoser]
public class PixelSamplingBenchmarks : IDisposable
{
    private readonly OcrPipeline _ocr = new();
    private readonly BitmapBounds _roi = new() { X = 1160, Y = 640, Width = 96, Height = 24 };
    private SoftwareBitmap _frame = null!;
    private PixelStrip _strip = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _frame = BenchmarkBitmapFactory.CreateFrame();
        _strip = await PixelStrip.CaptureAsync(_ocr, _frame, _roi);
    }

    [Benchmark]
    public async Task<PixelStrip> CapturePixels()
        => await PixelStrip.CaptureAsync(_ocr, _frame, _roi);

    [Benchmark]
    public RoiResult SerializePixels()
        => new()
        {
            RoiId = "pixel-strip",
            Kind = RoiResultKind.Pixels,
            PixelsBgra = ByteString.CopyFrom(_strip.Bgra),
            PixelsStride = (uint)_strip.Stride,
            PixelsWidth = (uint)_strip.Width,
            PixelsHeight = (uint)_strip.Height,
        };

    [GlobalCleanup]
    public void Dispose() => _frame.Dispose();
}
