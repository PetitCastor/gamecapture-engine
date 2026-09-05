using BenchmarkDotNet.Attributes;
using Windows.Graphics.Imaging;

namespace Ocrx.Engine.Benchmarks;

[MemoryDiagnoser]
public class OcrCropScaleBenchmarks : IDisposable
{
    private readonly OcrPipeline _ocr = new();
    private readonly BitmapBounds _roi = new() { X = 840, Y = 520, Width = 420, Height = 120 };
    private SoftwareBitmap _frame = null!;

    [GlobalSetup]
    public void Setup() => _frame = BenchmarkBitmapFactory.CreateFrame();

    [Benchmark]
    public async Task CropAndScale_TextPanel()
    {
        using var crop = await _ocr.CropAndScaleAsync(_frame, _roi, 2.5);
    }

    [GlobalCleanup]
    public void Dispose() => _frame.Dispose();
}
