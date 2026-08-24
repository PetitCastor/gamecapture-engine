using BenchmarkDotNet.Attributes;
using GameCapture.Contracts.Proto;
using Windows.Graphics.Imaging;

namespace GameCapture.Engine.Benchmarks;

[MemoryDiagnoser]
public class RepeatedRoiBenchmarks
{
    private readonly RoiSpec _first = new()
    {
        Id = "first",
        Mode = RoiMode.Pixels,
        Rect = new Rect { X = 1100, Y = 600, Width = 120, Height = 32 },
    };
    private readonly RoiSpec _equivalent = new()
    {
        Id = "equivalent",
        Mode = RoiMode.Pixels,
        Rect = new Rect { X = 1100, Y = 600, Width = 120, Height = 32 },
    };
    private readonly RoiSpec _unique = new()
    {
        Id = "unique",
        Mode = RoiMode.Pixels,
        Rect = new Rect { X = 1280, Y = 720, Width = 120, Height = 32 },
    };

    private readonly CancellationTokenSource _cts = new();
    private readonly ConsoleSink _sink = new();
    private BenchmarkFrameSource _source = null!;
    private ScanLoop _loop = null!;
    private ClientSubscription _firstClient = null!;
    private ClientSubscription _secondClient = null!;
    private Task _run = null!;
    private int _configuredWorkload;

    [GlobalSetup]
    public void Setup()
    {
        _source = new BenchmarkFrameSource();
        var ocr = new OcrPipeline();
        var status = new EngineStatus(ocr.LanguageTag, replayMode: true);
        var registry = new SubscriptionRegistry(status);
        _loop = new ScanLoop(
            _source,
            ocr,
            registry,
            status,
            _sink,
            new EngineConfig(),
            verbose: false);

        _firstClient = registry.Register(replayMode: true);
        _secondClient = registry.Register(replayMode: true);
        _firstClient.SetRois(new RoiSetUpdate { Rois = { _first } });
        _secondClient.SetRois(new RoiSetUpdate { Rois = { _equivalent } });
        _configuredWorkload = 1;
        _run = _loop.RunAsync(_cts.Token);
    }

    [Benchmark]
    public Task<(TrackResponse First, TrackResponse Second)> RepeatedEquivalentPixelRois()
        => RunTickAsync(_equivalent, workload: 1);

    [Benchmark]
    public Task<(TrackResponse First, TrackResponse Second)> UniquePixelRois()
        => RunTickAsync(_unique, workload: 2);

    private async Task<(TrackResponse First, TrackResponse Second)> RunTickAsync(
        RoiSpec second,
        int workload)
    {
        if (_configuredWorkload != workload)
        {
            _firstClient.SetRois(new RoiSetUpdate { Rois = { _first } });
            _secondClient.SetRois(new RoiSetUpdate { Rois = { second } });
            _configuredWorkload = workload;
        }

        _source.Publish(new SoftwareBitmap(
            BitmapPixelFormat.Bgra8,
            2560,
            1440,
            BitmapAlphaMode.Ignore));

        var first = await _firstClient.Out.Reader.ReadAsync(_cts.Token);
        var secondResult = await _secondClient.Out.Reader.ReadAsync(_cts.Token);
        return (first, secondResult);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _cts.CancelAsync();
        await _run;
        _loop.Dispose();
        _source.Dispose();
        _sink.Dispose();
        _cts.Dispose();
    }
}
