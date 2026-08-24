using System.Reflection;
using BenchmarkDotNet.Attributes;
using Windows.Graphics.Imaging;

namespace GameCapture.Engine.Benchmarks;

[MemoryDiagnoser]
public class RetainedFrameBenchmarks : IDisposable
{
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

        typeof(ScanLoop)
            .GetField("_lastScanned", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(_loop, _frame);
    }

    [Benchmark]
    public async Task<int> ReadRetainedFrameUnderGate()
    {
        await _loop.FrameGate.WaitAsync();
        try
        {
            return _loop.RetainedFrame?.PixelWidth ?? 0;
        }
        finally
        {
            _loop.FrameGate.Release();
        }
    }

    [GlobalCleanup]
    public void Dispose()
    {
        _loop.Dispose();
    }
}
