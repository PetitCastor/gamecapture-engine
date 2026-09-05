using System.Reflection;
using BenchmarkDotNet.Attributes;
using Ocrx.Contracts.Proto;
using Windows.Graphics.Imaging;

namespace Ocrx.Engine.Benchmarks;

[MemoryDiagnoser]
public class RetainedFrameBenchmarks : IDisposable
{
    private readonly RoiSpec _pixelRoi = new()
    {
        Id = "retained-pixels",
        Mode = RoiMode.Pixels,
        Rect = new Rect { X = 1160, Y = 640, Width = 96, Height = 24 },
    };
    private readonly SemaphoreSlim _serializedGate = new(1, 1);
    private readonly ScanLoop _loop;
    private readonly SoftwareBitmap _frame = BenchmarkBitmapFactory.CreateFrame();

    public RetainedFrameBenchmarks()
    {
        var status = new EngineStatus("en-US", replayMode: false);
        _loop = new ScanLoop(
            new DummyFrameSource(),
            new OcrPipeline(),
            new SubscriptionRegistry(status),
            status,
            new ConsoleSink(),
            new EngineConfig(),
            verbose: false);

        var store = (RetainedFrameStore)typeof(ScanLoop)
            .GetField("_retainedFrameStore", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(_loop)!;
        store.SwapAsync(_frame, manualFrameHandler: null).GetAwaiter().GetResult();
    }

    [Benchmark(Baseline = true)]
    public Task<int> TwoSerializedPixelReads()
        => RunTwoReadsAsync(serialized: true);

    [Benchmark]
    public Task<int> TwoLeasedPixelReads()
        => RunTwoReadsAsync(serialized: false);

    [GlobalCleanup]
    public void Dispose()
    {
        _loop.Dispose();
        _serializedGate.Dispose();
    }

    private async Task<int> RunTwoReadsAsync(bool serialized)
    {
        var first = ReadPixelsAsync(serialized);
        var second = ReadPixelsAsync(serialized);
        var results = await Task.WhenAll(first, second);
        return results[0] + results[1];
    }

    private async Task<int> ReadPixelsAsync(bool serialized)
    {
        if (serialized)
            await _serializedGate.WaitAsync();

        try
        {
            using var lease = await _loop.AcquireRetainedFrameLeaseAsync(CancellationToken.None);
            var result = await _loop.ReadOneAsync(lease!.Bitmap, _pixelRoi);
            return result.PixelsBgra.Length;
        }
        finally
        {
            if (serialized)
                _serializedGate.Release();
        }
    }
}
